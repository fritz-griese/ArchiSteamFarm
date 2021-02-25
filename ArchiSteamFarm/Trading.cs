//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2020 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Text;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace ArchiSteamFarm {
	public sealed class Trading : IDisposable {
		internal const byte MaxItemsPerTrade = byte.MaxValue; // This is decided upon various factors, mainly stability of Steam servers when dealing with huge trade offers
		internal const byte MaxTradesPerAccount = 5; // This is limit introduced by Valve

		private readonly Bot Bot;
		private readonly ConcurrentHashSet<ulong> HandledTradeOfferIDs = new ConcurrentHashSet<ulong>();
		private readonly SemaphoreSlim TradesSemaphore = new SemaphoreSlim(1, 1);

		private bool ParsingScheduled;

		internal Trading(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public void Dispose() => TradesSemaphore.Dispose();

		[PublicAPI]
		public static bool IsFairExchange(IReadOnlyCollection<Steam.Asset> itemsToGive, IReadOnlyCollection<Steam.Asset> itemsToReceive) {
			if ((itemsToGive == null) || (itemsToGive.Count == 0) || (itemsToReceive == null) || (itemsToReceive.Count == 0)) {
				throw new ArgumentNullException(nameof(itemsToGive) + " || " + nameof(itemsToReceive));
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), uint> itemsToGiveAmounts = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), uint>();

			foreach (Steam.Asset item in itemsToGive) {
				(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key = (item.RealAppID, item.Type, item.Rarity);
				itemsToGiveAmounts[key] = itemsToGiveAmounts.TryGetValue(key, out uint amount) ? amount + item.Amount : item.Amount;
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), uint> itemsToReceiveAmounts = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), uint>();

			foreach (Steam.Asset item in itemsToReceive) {
				(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key = (item.RealAppID, item.Type, item.Rarity);
				itemsToReceiveAmounts[key] = itemsToReceiveAmounts.TryGetValue(key, out uint amount) ? amount + item.Amount : item.Amount;
			}

			// Ensure that amount of items to give is at least amount of items to receive (per all fairness factors)
			foreach (((uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key, uint amountToGive) in itemsToGiveAmounts) {
				if (!itemsToReceiveAmounts.TryGetValue(key, out uint amountToReceive) || (amountToGive > amountToReceive)) {
					return false;
				}
			}

			return true;
		}

		[PublicAPI]
		public static bool IsTradeNeutralOrBetter(HashSet<Steam.Asset> inventory, ISet<Steam.Asset> itemsToGive, ISet<Steam.Asset> itemsToReceive) {
			if ((inventory == null) || (inventory.Count == 0) || (itemsToGive == null) || (itemsToGive.Count == 0) || (itemsToReceive == null) || (itemsToReceive.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory) + " || " + nameof(itemsToGive) + " || " + nameof(itemsToReceive));
			}

			// Input of this function is items we're expected to give/receive and our inventory (limited to realAppIDs of itemsToGive/itemsToReceive)
			// The objective is to determine whether the new state is beneficial (or at least neutral) towards us
			// There are a lot of factors involved here - different realAppIDs, different item types, possibility of user overpaying and more
			// All of those cases should be verified by our unit tests to ensure that the logic here matches all possible cases, especially those that were incorrectly handled previously

			// Firstly we get initial sets state of our inventory
			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), List<uint>> initialSets = GetInventorySets(inventory);

			// Once we have initial state, we remove items that we're supposed to give from our inventory
			// This loop is a bit more complex due to the fact that we might have a mix of the same item splitted into different amounts
			foreach (Steam.Asset itemToGive in itemsToGive) {
				uint amountToGive = itemToGive.Amount;
				HashSet<Steam.Asset> itemsToRemove = new HashSet<Steam.Asset>();

				// Keep in mind that ClassID is unique only within appID scope - we can do it like this because we're not dealing with non-Steam items here (otherwise we'd need to check appID too)
				foreach (Steam.Asset item in inventory.Where(item => item.ClassID == itemToGive.ClassID)) {
					if (amountToGive >= item.Amount) {
						itemsToRemove.Add(item);
						amountToGive -= item.Amount;
					} else {
						item.Amount -= amountToGive;
						amountToGive = 0;
					}

					if (amountToGive == 0) {
						break;
					}
				}

				if (amountToGive > 0) {
					throw new ArgumentNullException(nameof(amountToGive));
				}

				if (itemsToRemove.Count > 0) {
					inventory.ExceptWith(itemsToRemove);
				}
			}

			// Now we can add items that we're supposed to receive, this one doesn't require advanced amounts logic since we can just add items regardless
			foreach (Steam.Asset itemToReceive in itemsToReceive) {
				inventory.Add(itemToReceive);
			}

			// Now we can get final sets state of our inventory after the exchange
			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), List<uint>> finalSets = GetInventorySets(inventory);

			// Once we have both states, we can check overall fairness
			foreach (((uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) set, List<uint> beforeAmounts) in initialSets) {
				if (!finalSets.TryGetValue(set, out List<uint>? afterAmounts)) {
					// If we have no info about this set, then it has to be a bad one
					return false;
				}

				// If amount of unique items in the set decreases, this is always a bad trade (e.g. 1 1 -> 0 2)
				if (afterAmounts.Count < beforeAmounts.Count) {
					return false;
				}

				// If amount of unique items in the set increases, this is always a good trade (e.g. 0 2 -> 1 1)
				if (afterAmounts.Count > beforeAmounts.Count) {
					continue;
				}

				// At this point we're sure that amount of unique items stays the same, so we can evaluate actual sets
				// We make use of the fact that our amounts are already sorted in ascending order, so we can just take the first value instead of calculating ourselves
				uint beforeSets = beforeAmounts[0];
				uint afterSets = afterAmounts[0];

				// If amount of our sets for this game decreases, this is always a bad trade (e.g. 2 2 2 -> 3 2 1)
				if (afterSets < beforeSets) {
					return false;
				}

				// If amount of our sets for this game increases, this is always a good trade (e.g. 3 2 1 -> 2 2 2)
				if (afterSets > beforeSets) {
					continue;
				}

				// At this point we're sure that both number of unique items in the set stays the same, as well as number of our actual sets
				// We need to ensure set progress here and keep in mind overpaying, so we'll calculate neutrality as a difference in amounts at appropriate indexes
				// Neutrality can't reach value below 0 at any single point of calculation, as that would imply a loss of progress even if we'd end up with a positive value by the end
				int neutrality = 0;

				for (byte i = 0; i < afterAmounts.Count; i++) {
					neutrality += (int) (afterAmounts[i] - beforeAmounts[i]);

					if (neutrality < 0) {
						return false;
					}
				}
			}

			// If we didn't find any reason above to reject this trade, it's at least neutral+ for us - it increases our progress towards badge completion
			return true;
		}

		internal static (Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> FullState, Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> TradableState) GetDividedInventoryState(IReadOnlyCollection<Steam.Asset> inventory) {
			if ((inventory == null) || (inventory.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory));
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> fullState = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>>();
			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> tradableState = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>>();

			foreach (Steam.Asset item in inventory) {
				(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key = (item.RealAppID, item.Type, item.Rarity);

				if (fullState.TryGetValue(key, out Dictionary<ulong, uint>? fullSet)) {
					fullSet[item.ClassID] = fullSet.TryGetValue(item.ClassID, out uint amount) ? amount + item.Amount : item.Amount;
				} else {
					fullState[key] = new Dictionary<ulong, uint> { { item.ClassID, item.Amount } };
				}

				if (!item.Tradable) {
					continue;
				}

				if (tradableState.TryGetValue(key, out Dictionary<ulong, uint>? tradableSet)) {
					tradableSet[item.ClassID] = tradableSet.TryGetValue(item.ClassID, out uint amount) ? amount + item.Amount : item.Amount;
				} else {
					tradableState[key] = new Dictionary<ulong, uint> { { item.ClassID, item.Amount } };
				}
			}

			return (fullState, tradableState);
		}

		internal static Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> GetTradableInventoryState(IReadOnlyCollection<Steam.Asset> inventory) {
			if ((inventory == null) || (inventory.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory));
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> tradableState = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>>();

			foreach (Steam.Asset item in inventory.Where(item => item.Tradable)) {
				(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key = (item.RealAppID, item.Type, item.Rarity);

				if (tradableState.TryGetValue(key, out Dictionary<ulong, uint>? tradableSet)) {
					tradableSet[item.ClassID] = tradableSet.TryGetValue(item.ClassID, out uint amount) ? amount + item.Amount : item.Amount;
				} else {
					tradableState[key] = new Dictionary<ulong, uint> { { item.ClassID, item.Amount } };
				}
			}

			return tradableState;
		}

		internal static HashSet<Steam.Asset> GetTradableItemsFromInventory(ISet<Steam.Asset> inventory, IDictionary<ulong, uint> classIDs) {
			if ((inventory == null) || (inventory.Count == 0) || (classIDs == null) || (classIDs.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory) + " || " + nameof(classIDs));
			}

			HashSet<Steam.Asset> result = new HashSet<Steam.Asset>();

			foreach (Steam.Asset item in inventory.Where(item => item.Tradable)) {
				if (!classIDs.TryGetValue(item.ClassID, out uint amount)) {
					continue;
				}

				if (amount < item.Amount) {
					item.Amount = amount;
				}

				result.Add(item);

				if (amount == item.Amount) {
					classIDs.Remove(item.ClassID);
				} else {
					classIDs[item.ClassID] = amount - item.Amount;
				}
			}

			return result;
		}

		internal static bool IsEmptyForMatching(IReadOnlyDictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> fullState, IReadOnlyDictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> tradableState) {
			if ((fullState == null) || (tradableState == null)) {
				throw new ArgumentNullException(nameof(fullState) + " || " + nameof(tradableState));
			}

			foreach (((uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) set, Dictionary<ulong, uint> state) in tradableState) {
				if (!fullState.TryGetValue(set, out Dictionary<ulong, uint>? fullSet) || (fullSet.Count == 0)) {
					throw new ArgumentNullException(nameof(fullSet));
				}

				if (!IsEmptyForMatching(fullSet, state)) {
					return false;
				}
			}

			// We didn't find any matchable combinations, so this inventory is empty
			return true;
		}

		internal static bool IsEmptyForMatching(IReadOnlyDictionary<ulong, uint> fullSet, IReadOnlyDictionary<ulong, uint> tradableSet) {
			if ((fullSet == null) || (tradableSet == null)) {
				throw new ArgumentNullException(nameof(fullSet) + " || " + nameof(tradableSet));
			}

			foreach ((ulong classID, uint amount) in tradableSet) {
				switch (amount) {
					case 0:
						// No tradable items, this should never happen, dictionary should not have this key to begin with
						throw new ArgumentOutOfRangeException(nameof(amount));
					case 1:
						// Single tradable item, can be matchable or not depending on the rest of the inventory
						if (!fullSet.TryGetValue(classID, out uint fullAmount) || (fullAmount == 0) || (fullAmount < amount)) {
							throw new ArgumentNullException(nameof(fullAmount));
						}

						if (fullAmount > 1) {
							// If we have a single tradable item but more than 1 in total, this is matchable
							return false;
						}

						// A single exclusive tradable item is not matchable, continue
						continue;
					default:
						// Any other combination of tradable items is always matchable
						return false;
				}
			}

			// We didn't find any matchable combinations, so this inventory is empty
			return true;
		}

		internal void OnDisconnected() => HandledTradeOfferIDs.Clear();

		internal async Task OnNewTrade() {
			// We aim to have a maximum of 2 tasks, one already working, and one waiting in the queue
			// This way we can call this function as many times as needed e.g. because of Steam events
			lock (TradesSemaphore) {
				if (ParsingScheduled) {
					return;
				}

				ParsingScheduled = true;
			}

			await TradesSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				bool lootableTypesReceived;

				using (await Bot.Actions.GetTradingLock().ConfigureAwait(false)) {
					lock (TradesSemaphore) {
						ParsingScheduled = false;
					}

					lootableTypesReceived = await ParseActiveTrades().ConfigureAwait(false);
				}

				if (lootableTypesReceived && Bot.BotConfig.SendOnFarmingFinished && (Bot.BotConfig.LootableTypes.Count > 0)) {
					await Bot.Actions.SendInventory(filterFunction: item => Bot.BotConfig.LootableTypes.Contains(item.Type)).ConfigureAwait(false);
				}
			} finally {
				TradesSemaphore.Release();
			}
		}

		private static Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), List<uint>> GetInventorySets(IReadOnlyCollection<Steam.Asset> inventory) {
			if ((inventory == null) || (inventory.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory));
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> sets = GetInventoryState(inventory);

			return sets.ToDictionary(set => set.Key, set => set.Value.Values.OrderBy(amount => amount).ToList());
		}

		private static Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> GetInventoryState(IReadOnlyCollection<Steam.Asset> inventory) {
			if ((inventory == null) || (inventory.Count == 0)) {
				throw new ArgumentNullException(nameof(inventory));
			}

			Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>> state = new Dictionary<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity), Dictionary<ulong, uint>>();

			foreach (Steam.Asset item in inventory) {
				(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity) key = (item.RealAppID, item.Type, item.Rarity);

				if (state.TryGetValue(key, out Dictionary<ulong, uint>? set)) {
					set[item.ClassID] = set.TryGetValue(item.ClassID, out uint amount) ? amount + item.Amount : item.Amount;
				} else {
					state[key] = new Dictionary<ulong, uint> { { item.ClassID, item.Amount } };
				}
			}

			return state;
		}

		private async Task<bool> ParseActiveTrades() {
			HashSet<Steam.TradeOffer>? tradeOffers = await Bot.ArchiWebHandler.GetActiveTradeOffers().ConfigureAwait(false);

			if ((tradeOffers == null) || (tradeOffers.Count == 0)) {
				return false;
			}

			if (HandledTradeOfferIDs.Count > 0) {
				HandledTradeOfferIDs.IntersectWith(tradeOffers.Select(tradeOffer => tradeOffer.TradeOfferID));
			}

			IEnumerable<Task<(ParseTradeResult? TradeResult, bool RequiresMobileConfirmation)>> tasks = tradeOffers.Where(tradeOffer => !HandledTradeOfferIDs.Contains(tradeOffer.TradeOfferID)).Select(ParseTrade);
			IList<(ParseTradeResult? TradeResult, bool RequiresMobileConfirmation)> results = await Utilities.InParallel(tasks).ConfigureAwait(false);

			if (Bot.HasMobileAuthenticator) {
				HashSet<ulong> mobileTradeOfferIDs = results.Where(result => (result.TradeResult != null) && (result.TradeResult.Result == ParseTradeResult.EResult.Accepted) && result.RequiresMobileConfirmation).Select(result => result.TradeResult!.TradeOfferID).ToHashSet();

				if (mobileTradeOfferIDs.Count > 0) {
					(bool twoFactorSuccess, _) = await Bot.Actions.HandleTwoFactorAuthenticationConfirmations(true, MobileAuthenticator.Confirmation.EType.Trade, mobileTradeOfferIDs, true).ConfigureAwait(false);

					if (!twoFactorSuccess) {
						HandledTradeOfferIDs.ExceptWith(mobileTradeOfferIDs);

						return false;
					}
				}
			}

			HashSet<ParseTradeResult> validTradeResults = results.Where(result => result.TradeResult != null).Select(result => result.TradeResult!).ToHashSet();

			if (validTradeResults.Count > 0) {
				await PluginsCore.OnBotTradeOfferResults(Bot, validTradeResults).ConfigureAwait(false);
			}

			return results.Any(result => (result.TradeResult != null) && (result.TradeResult.Result == ParseTradeResult.EResult.Accepted) && (!result.RequiresMobileConfirmation || Bot.HasMobileAuthenticator) && (result.TradeResult.ReceivedItemTypes?.Any(receivedItemType => Bot.BotConfig.LootableTypes.Contains(receivedItemType)) == true));
		}

		private async Task<(ParseTradeResult? TradeResult, bool RequiresMobileConfirmation)> ParseTrade(Steam.TradeOffer tradeOffer) {
			if (tradeOffer == null) {
				throw new ArgumentNullException(nameof(tradeOffer));
			}

			if (tradeOffer.State != ETradeOfferState.Active) {
				Bot.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, tradeOffer.State));

				return (null, false);
			}

			if (!HandledTradeOfferIDs.Add(tradeOffer.TradeOfferID)) {
				// We've already seen this trade, this should not happen
				Bot.ArchiLogger.LogGenericError(string.Format(Strings.IgnoringTrade, tradeOffer.TradeOfferID));

				return (new ParseTradeResult(tradeOffer.TradeOfferID, ParseTradeResult.EResult.Ignored, tradeOffer.ItemsToReceive), false);
			}

			ParseTradeResult.EResult result = await ShouldAcceptTrade(tradeOffer).ConfigureAwait(false);
			bool tradeRequiresMobileConfirmation = false;

			switch (result) {
				case ParseTradeResult.EResult.Ignored:
				case ParseTradeResult.EResult.Rejected:
					bool accept = await PluginsCore.OnBotTradeOffer(Bot, tradeOffer).ConfigureAwait(false);

					if (accept) {
						result = ParseTradeResult.EResult.Accepted;
					}

					break;
			}

			switch (result) {
				case ParseTradeResult.EResult.Accepted:
					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.AcceptingTrade, tradeOffer.TradeOfferID));

					(bool success, bool requiresMobileConfirmation) = await Bot.ArchiWebHandler.AcceptTradeOffer(tradeOffer.TradeOfferID).ConfigureAwait(false);

					if (!success) {
						result = ParseTradeResult.EResult.TryAgain;

						goto case ParseTradeResult.EResult.TryAgain;
					}

					if (tradeOffer.ItemsToReceive.Sum(item => item.Amount) > tradeOffer.ItemsToGive.Sum(item => item.Amount)) {
						Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.BotAcceptedDonationTrade, tradeOffer.TradeOfferID));
					}

					tradeRequiresMobileConfirmation = requiresMobileConfirmation;

					break;
				case ParseTradeResult.EResult.Blacklisted:
				case ParseTradeResult.EResult.Rejected when Bot.BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.RejectInvalidTrades):
					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.RejectingTrade, tradeOffer.TradeOfferID));

					if (!await Bot.ArchiWebHandler.DeclineTradeOffer(tradeOffer.TradeOfferID).ConfigureAwait(false)) {
						result = ParseTradeResult.EResult.TryAgain;

						goto case ParseTradeResult.EResult.TryAgain;
					}

					break;
				case ParseTradeResult.EResult.Ignored:
				case ParseTradeResult.EResult.Rejected:
					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.IgnoringTrade, tradeOffer.TradeOfferID));

					break;
				case ParseTradeResult.EResult.TryAgain:
					HandledTradeOfferIDs.Remove(tradeOffer.TradeOfferID);

					goto case ParseTradeResult.EResult.Ignored;
				default:
					Bot.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(result), result));

					return (null, false);
			}

			return (new ParseTradeResult(tradeOffer.TradeOfferID, result, tradeOffer.ItemsToReceive), tradeRequiresMobileConfirmation);
		}

		private async Task<int> GetSetSize(string realAppId) {
			IDocument badgeInfoPage = await Bot.ArchiWebHandler.GetBadgeInfoPage(realAppId).ConfigureAwait(false);

			if (badgeInfoPage == null) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve set size for app {realAppId}.");
				return -1;
			}

			IElement infoText = badgeInfoPage.All.Where(tag => tag.ClassName == "badge_card_set_text ellipsis" && tag.Children.Count() == 0).First();

			Match match = Regex.Match(infoText.Text().Trim(), @"\d+\s*of\s*(\d+),");

			if (!match.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve set size for app {realAppId}.");
				return -1;
			}

			return match.Groups[1].Value.ToInteger(-1);
		}

		// To get the order histogramm, we have to obtain the item_nameid, which can be found in a script tag on the market page.
		private async Task<Steam.OrderHistogram> GetOrders(Steam.Asset asset) {
			string marketHashName = asset.AdditionalProperties["market_hash_name"].ToString();
			if (marketHashName == null) {
				Bot.ArchiLogger.LogGenericDebug($"Asset {asset.InfoText} has no market_hash_name. Cannot get orders.");
				return null;
			}

			IDocument marketPage = await Bot.ArchiWebHandler.GetMarketPage(asset.AppID.ToString(), marketHashName).ConfigureAwait(false);

			List<IElement> scripts = marketPage.All.Where(tag => tag.LocalName == "script" && !tag.TextContent.Trim().Equals("")).ToList();

			Match match = null;
			int i = scripts.Count;
			do {
				i--;
				match = Regex.Match(scripts[i].Text(), @"Market_LoadOrderSpread\(\s*(\d+)\s*\)");
			} while (i > 0 && !match.Success);

			if (!match.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve item_nameid for asset {asset.InfoText} from market page. Therfore we cannot obtain the order histogram.");
				return null;
			}

			string nameId = match.Groups[1].Value;

			Steam.OrderHistogram orders = await Bot.ArchiWebHandler.getOrderHistorgramm(nameId).ConfigureAwait(false);

			if (!orders.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve order histogramm for asset {asset.InfoText}.");

				return null;
			}

			return orders;
		}

		private async Task<int> GetRecentlySold(Steam.Asset asset) {
			string marketHashName = asset.AdditionalProperties["market_hash_name"].ToString();

			if (marketHashName == null || marketHashName.StripLeadingTrailingSpaces() == "") {
				Bot.ArchiLogger.LogGenericDebug($"Asset {asset.InfoText} has no market_hash_name. Cannot get number of recently sold items.");

				return -1;
			}

			if (!asset.Marketable) {
				Bot.ArchiLogger.LogGenericDebug($"Asset {asset.InfoText} is not marketable. Cannot get number of recently sold items.");

				return -1;
			}

			Steam.PriceOverview priceOverview = await Bot.ArchiWebHandler.getPriceOverview(asset.AppID.ToString(), marketHashName).ConfigureAwait(false);

			if (!priceOverview.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve recently sold items for asset {asset.InfoText}.");

				return -1;
			}

			return priceOverview.Volume;
		}

		private async Task<int> GetGooValue(Steam.Asset asset) {
			if (!(asset.Type == Steam.Asset.EType.Emoticon || asset.Type == Steam.Asset.EType.FoilTradingCard || asset.Type == Steam.Asset.EType.TradingCard || asset.Type == Steam.Asset.EType.ProfileBackground || asset.Type == Steam.Asset.EType.SaleItem)) {
				Bot.ArchiLogger.LogGenericDebug($"Asset {asset.InfoText} cannot be dismanteled into gems.");

				return -1;
			}

			List<KeyValue> ownerActions = asset.AdditionalProperties["owner_actions"].Children;
			bool foundGrindGooLink = false;
			string link = null;

			foreach (KeyValue action in ownerActions) {
				if (action.Children[1].Value == "#TradingCards_GrindIntoGoo") {
					foundGrindGooLink = true;
					link = action.Children[0].Value;
					break;
				}
			}

			if (!foundGrindGooLink) {
				Bot.ArchiLogger.LogGenericError($"Could not find grind goo link for asset {asset.InfoText}.");
				return -1;
			}

			Match match = Regex.Match(link, @"javascript:GetGooValue\(\s*'.*',\s*'.*',\s*\d+,\s*(\d+),\s*.*\s*\)");

			if (!match.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve item_type for asset {asset.InfoText}. Cannot get goo value.");
				return -1;
			}

			string itemType = match.Groups[1].Value;

			Steam.GooResponse gooResponse = await Bot.ArchiWebHandler.getGooValue(asset.RealAppID.ToString(), itemType).ConfigureAwait(false);

			if (!gooResponse.Success) {
				Bot.ArchiLogger.LogGenericError($"Could not retrieve goo value for asset {asset.InfoText}.");

				return -1;
			}

			return gooResponse.GooValue;
		}

		private async Task<ParseTradeResult.EResult> ShouldAcceptTrade(Steam.TradeOffer tradeOffer) {
			if (Bot.Bots == null) {
				throw new ArgumentNullException(nameof(Bot.Bots));
			}

			if ((tradeOffer == null) || (ASF.GlobalConfig == null)) {
				throw new ArgumentNullException(nameof(tradeOffer) + " || " + nameof(ASF.GlobalConfig));
			}

			if (tradeOffer.OtherSteamID64 != 0) {
				// Always accept trades from SteamMasterID
				if (Bot.HasPermission(tradeOffer.OtherSteamID64, BotConfig.EPermission.Master)) {
					Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Accepted, nameof(tradeOffer.OtherSteamID64) + " " + tradeOffer.OtherSteamID64 + ": " + BotConfig.EPermission.Master));

					return ParseTradeResult.EResult.Accepted;
				}

				// Always deny trades from blacklisted steamIDs
				if (Bot.IsBlacklistedFromTrades(tradeOffer.OtherSteamID64)) {
					Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Blacklisted, nameof(tradeOffer.OtherSteamID64) + " " + tradeOffer.OtherSteamID64));

					return ParseTradeResult.EResult.Blacklisted;
				}
			}

			// Check if it's donation trade
			switch (tradeOffer.ItemsToGive.Count) {
				case 0 when tradeOffer.ItemsToReceive.Count == 0:
					// If it's steam issue, try again later
					Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.TryAgain, nameof(tradeOffer.ItemsToReceive.Count) + " = 0"));

					return ParseTradeResult.EResult.TryAgain;
				case 0:
					// Otherwise react accordingly, depending on our preference
					bool acceptDonations = Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.AcceptDonations);
					bool acceptBotTrades = !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.DontAcceptBotTrades);

					// If we accept donations and bot trades, accept it right away
					if (acceptDonations && acceptBotTrades) {
						Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Accepted, nameof(acceptDonations) + " = " + true + " && " + nameof(acceptBotTrades) + " = " + true));

						return ParseTradeResult.EResult.Accepted;
					}

					// If we don't accept donations, neither bot trades, deny it right away
					if (!acceptDonations && !acceptBotTrades) {
						Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Rejected, nameof(acceptDonations) + " = " + false + " && " + nameof(acceptBotTrades) + " = " + false));

						return ParseTradeResult.EResult.Rejected;
					}

					// Otherwise we either accept donations but not bot trades, or we accept bot trades but not donations
					bool isBotTrade = (tradeOffer.OtherSteamID64 != 0) && Bot.Bots.Values.Any(bot => bot.SteamID == tradeOffer.OtherSteamID64);

					ParseTradeResult.EResult result = (acceptDonations && !isBotTrade) || (acceptBotTrades && isBotTrade) ? ParseTradeResult.EResult.Accepted : ParseTradeResult.EResult.Rejected;

					Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, result, nameof(acceptDonations) + " = " + acceptDonations + " && " + nameof(acceptBotTrades) + " = " + acceptBotTrades + " && " + nameof(isBotTrade) + " = " + isBotTrade));

					return result;
			}

			// If we don't have SteamTradeMatcher enabled, this is the end for us
			if (!Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.SteamTradeMatcher)) {
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Rejected, nameof(BotConfig.ETradingPreferences.SteamTradeMatcher) + " = " + false));

				return ParseTradeResult.EResult.Rejected;
			}

			// Decline trade if we're giving more count-wise, this is a very naive pre-check, it'll be strengthened in more detailed fair types exchange next
			if (tradeOffer.ItemsToGive.Count > tradeOffer.ItemsToReceive.Count) {
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Rejected, nameof(tradeOffer.ItemsToGive.Count) + ": " + tradeOffer.ItemsToGive.Count + " > " + tradeOffer.ItemsToReceive.Count));

				return ParseTradeResult.EResult.Rejected;
			}

			// Decline trade if we're requested to handle any not-accepted item type or if it's not fair games/types exchange
			if (!tradeOffer.IsValidSteamItemsRequest(Bot.BotConfig.MatchableTypes) || !IsFairExchange(tradeOffer.ItemsToGive, tradeOffer.ItemsToReceive)) {
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Rejected, nameof(tradeOffer.IsValidSteamItemsRequest) + " || " + nameof(IsFairExchange)));

				return ParseTradeResult.EResult.Rejected;
			}

			// At this point we're sure that STM trade is valid

			// Fetch trade hold duration
			byte? holdDuration = await Bot.GetTradeHoldDuration(tradeOffer.OtherSteamID64, tradeOffer.TradeOfferID).ConfigureAwait(false);

			if (!holdDuration.HasValue) {
				// If we can't get trade hold duration, try again later
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.TryAgain, nameof(holdDuration)));

				return ParseTradeResult.EResult.TryAgain;
			}

			// If user has a trade hold, we add extra logic
			if (holdDuration.Value > 0) {
				// If trade hold duration exceeds our max, or user asks for cards with short lifespan, reject the trade
				if ((holdDuration.Value > ASF.GlobalConfig.MaxTradeHoldDuration) || tradeOffer.ItemsToGive.Any(item => ((item.Type == Steam.Asset.EType.FoilTradingCard) || (item.Type == Steam.Asset.EType.TradingCard)) && CardsFarmer.SalesBlacklist.Contains(item.RealAppID))) {
					Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Rejected, nameof(holdDuration) + " > 0: " + holdDuration.Value));

					return ParseTradeResult.EResult.Rejected;
				}
			}

			// If we're matching everything, this is enough for us
			if (Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything)) {
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.Accepted, BotConfig.ETradingPreferences.MatchEverything));

				return ParseTradeResult.EResult.Accepted;
			}

			// Get sets we're interested in
			HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)> wantedSets = new HashSet<(uint RealAppID, Steam.Asset.EType Type, Steam.Asset.ERarity Rarity)>();

			foreach (Steam.Asset item in tradeOffer.ItemsToGive) {
				wantedSets.Add((item.RealAppID, item.Type, item.Rarity));
			}

			// Now check if it's worth for us to do the trade
			HashSet<Steam.Asset> inventory;

			try {
				inventory = await Bot.ArchiWebHandler.GetInventoryAsync(Bot.SteamID).Where(item => wantedSets.Contains((item.RealAppID, item.Type, item.Rarity))).ToHashSetAsync().ConfigureAwait(false);
			} catch (HttpRequestException e) {
				// If we can't check our inventory when not using MatchEverything, this is a temporary failure, try again later
				Bot.ArchiLogger.LogGenericWarningException(e);
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.TryAgain, nameof(inventory)));

				return ParseTradeResult.EResult.TryAgain;
			} catch (Exception e) {
				// If we can't check our inventory when not using MatchEverything, this is a temporary failure, try again later
				Bot.ArchiLogger.LogGenericException(e);
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.TryAgain, nameof(inventory)));

				return ParseTradeResult.EResult.TryAgain;
			}

			if (inventory.Count == 0) {
				// If we can't check our inventory when not using MatchEverything, this is a temporary failure, try again later
				Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsEmpty, nameof(inventory)));
				Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, ParseTradeResult.EResult.TryAgain, nameof(inventory)));

				return ParseTradeResult.EResult.TryAgain;
			}

			bool accept = IsTradeNeutralOrBetter(inventory, tradeOffer.ItemsToGive.Select(item => item.CreateShallowCopy()).ToHashSet(), tradeOffer.ItemsToReceive.Select(item => item.CreateShallowCopy()).ToHashSet());

			// We're now sure whether the trade is neutral+ for us or not
			ParseTradeResult.EResult acceptResult = accept ? ParseTradeResult.EResult.Accepted : ParseTradeResult.EResult.Rejected;

			Bot.ArchiLogger.LogGenericDebug(string.Format(Strings.BotTradeOfferResult, tradeOffer.TradeOfferID, acceptResult, nameof(IsTradeNeutralOrBetter)));

			return acceptResult;
		}

		public sealed class ParseTradeResult {
			[PublicAPI]
			public readonly EResult Result;

			[PublicAPI]
			public readonly ulong TradeOfferID;

			internal readonly ImmutableHashSet<Steam.Asset.EType>? ReceivedItemTypes;

			internal ParseTradeResult(ulong tradeOfferID, EResult result, IReadOnlyCollection<Steam.Asset>? itemsToReceive = null) {
				if ((tradeOfferID == 0) || (result == EResult.Unknown)) {
					throw new ArgumentNullException(nameof(tradeOfferID) + " || " + nameof(result));
				}

				TradeOfferID = tradeOfferID;
				Result = result;

				if ((itemsToReceive != null) && (itemsToReceive.Count > 0)) {
					ReceivedItemTypes = itemsToReceive.Select(item => item.Type).ToImmutableHashSet();
				}
			}

			public enum EResult : byte {
				Unknown,
				Accepted,
				Blacklisted,
				Ignored,
				Rejected,
				TryAgain
			}
		}
	}
}
