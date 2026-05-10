using System;
using System.Collections.Generic;
using System.Linq;
using Data;
using Data.ScriptableObject;
using Extensions;
using Game;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;

namespace TedditQueuedConstruction
{
    internal static class FacilityQueue
    {
        internal const string QueuedTooltipHeader = "<color=#FFD66B>Queued: awaiting resources.</color>";
        private static readonly Dictionary<Facility, int> DisplayStackCounts = new Dictionary<Facility, int>();
        private static readonly Dictionary<Facility, int> DisplayStackQueuedCounts = new Dictionary<Facility, int>();
        private static readonly Dictionary<Facility, int> DisplayStackBuildingCounts = new Dictionary<Facility, int>();
        private static readonly AccessTools.FieldRef<ProductionItem, bool> StartBuildRef =
            AccessTools.FieldRefAccess<ProductionItem, bool>("startBuild");

        internal static bool IsQueued(Facility facility)
        {
            return facility != null && !facility.FinishConstructionBool && !facility.StartBuild;
        }

        internal static List<Facility> BuildFacilityPanelRows(ObjectInfoData data)
        {
            DisplayStackCounts.Clear();
            DisplayStackQueuedCounts.Clear();
            DisplayStackBuildingCounts.Clear();

            List<Facility> rows = new List<Facility>();
            List<Facility> facilities = data.ListFacility
                .Where(facility => facility.facilityDescriptor != null && facility.facilityDescriptor.ShowOnUI)
                .ToList();

            rows.AddRange(facilities.Where(facility => facility.FinishConstructionBool));

            foreach (IGrouping<FacilityBaseDescriptor, Facility> group in facilities.Where(facility => !facility.FinishConstructionBool).GroupBy(facility => facility.facilityDescriptor))
            {
                Facility representative = group
                    .OrderBy(facility => IsQueued(facility) ? 1 : 0)
                    .ThenByDescending(facility => facility.BuildProgress)
                    .First();
                int total = group.Count();
                DisplayStackCounts[representative] = total;
                DisplayStackQueuedCounts[representative] = group.Count(IsQueued);
                DisplayStackBuildingCounts[representative] = total - DisplayStackQueuedCounts[representative];
                rows.Add(representative);
            }

            return rows.OrderBy(GetFacilitySortBucket).ToList();
        }

        internal static int GetDisplayStackCount(Facility facility)
        {
            if (facility != null && DisplayStackCounts.TryGetValue(facility, out int count))
            {
                return count;
            }
            return 1;
        }

        private static int GetDisplayQueuedCount(Facility facility)
        {
            if (facility != null && DisplayStackQueuedCounts.TryGetValue(facility, out int count))
            {
                return count;
            }
            return IsQueued(facility) ? 1 : 0;
        }

        private static int GetDisplayBuildingCount(Facility facility)
        {
            if (facility != null && DisplayStackBuildingCounts.TryGetValue(facility, out int count))
            {
                return count;
            }
            return facility != null && !facility.FinishConstructionBool && !IsQueued(facility) ? 1 : 0;
        }

        private static int GetFacilitySortBucket(Facility facility)
        {
            if (facility.FinishConstructionBool)
            {
                return 0;
            }
            if (IsQueued(facility))
            {
                return 2;
            }
            return 1;
        }

        internal static ResourcePrice GetPrice(FacilityBaseDescriptor descriptor, ObjectInfo objectInfo)
        {
            ResourcePrice price = descriptor.Price * MonoBehaviourSingleton<GameManager>.Instance.Player.BonusController.GetBonus(EBonus.BuildCost, descriptor, objectInfo);
            price.CeilToInt();
            return price;
        }

        internal static bool CanAfford(ObjectInfoData data, ResourcePrice price)
        {
            if (data == null || price == null)
            {
                return false;
            }
            foreach (ResourcePriceOne resource in price.ListResources)
            {
                if (data.CheckResources(resource.ResourceDefinition) < resource.Price)
                {
                    return false;
                }
            }
            return data.company.MoneyController.CurrentMoney >= price.BuildCost;
        }

        internal static ResourcePrice GetResourcesPresentForPrice(ObjectInfoData data, ResourcePrice price)
        {
            List<ResourcePriceOne> resources = new List<ResourcePriceOne>();
            if (data == null || price == null)
            {
                return new ResourcePrice(resources);
            }

            foreach (ResourcePriceOne resource in price.ListResources)
            {
                double present = Math.Max(0.0, data.CheckResources(resource.ResourceDefinition));
                resources.Add(new ResourcePriceOne(resource.ResourceDefinition, Math.Min(resource.Price, present)));
            }

            return new ResourcePrice(resources);
        }

        internal static bool TryShowMarketPurchasePopup(ObjectInfoData data, FacilityBaseDescriptor descriptor, ResourcePrice price)
        {
            if (data == null || descriptor == null || price == null)
            {
                return false;
            }

            ResourcePrice resourcesOnPlanet = GetResourcesPresentForPrice(data, price);
            ResourcePrice missing = price - resourcesOnPlanet;
            missing.CeilToInt();
            if (!TryBuildMarketPurchasePlan(data, missing, out List<MarketPurchase> purchases, out double marketCost))
            {
                return false;
            }

            double totalCost = marketCost + price.BuildCost;
            string text = Language.LEManager.Get("PopUp.NoMony2")
                .MyFormat(missing.ToStringTranslation(" <color=grey>/</color> "), totalCost.ToPostfixString() + Extensions.MyExtensions.DollarString);

            SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance.ShowPopUP(text, delegate
            {
                if (!BuyMissingResourcesFromMarket(data, purchases))
                {
                    QueueFacility(data, descriptor);
                    return;
                }

                if (data.RemoveResource(price))
                {
                    data.AddFacility(descriptor, prebuilt: false);
                    data.InvokeRefreshUIAddFacilityOrBuildProductItem();
                    Plugin.Log.LogInfo($"Bought market resources and started {descriptor.ID} on {data.ObjectInfo.ObjectName}.");
                }
                else
                {
                    QueueFacility(data, descriptor);
                }
            }, delegate
            {
                QueueFacility(data, descriptor);
            }, data.company.MoneyController.CurrentMoney >= totalCost, btnNoEnable: true, blockerOn: true, yesNoMenu: false, pauseTime: true, Language.LEManager.Get("Tooltip.ButtonInteraction.Offer.NoMony"));

            return true;
        }

        private static bool TryBuildMarketPurchasePlan(ObjectInfoData data, ResourcePrice missing, out List<MarketPurchase> purchases, out double marketCost)
        {
            purchases = new List<MarketPurchase>();
            marketCost = 0.0;
            if (data?.ObjectInfo == null || data.company == null || missing == null || MonoBehaviourSingleton<MarketOfferManager>.Instance == null)
            {
                return false;
            }

            bool needsMarketResource = false;
            foreach (ResourcePriceOne missingResource in missing.ListResources)
            {
                if (missingResource.ResourceDefinition == null || missingResource.Price <= 0.0)
                {
                    continue;
                }

                needsMarketResource = true;
                double remaining = missingResource.Price;
                List<Offer> offers = MonoBehaviourSingleton<MarketOfferManager>.Instance.Offerts
                    .Where(offer => offer != null
                        && !offer.OfferDone
                        && !offer.BuySell
                        && offer.Rd == missingResource.ResourceDefinition
                        && offer.WhereOffer == data.ObjectInfo
                        && offer.Company != data.company
                        && offer.CountLeft > 0.0)
                    .OrderBy(offer => offer.PricePerUnit)
                    .ToList();

                foreach (Offer offer in offers)
                {
                    double count = Math.Min(remaining, offer.CountLeft);
                    if (count <= 0.0)
                    {
                        continue;
                    }

                    purchases.Add(new MarketPurchase(offer, count));
                    marketCost += offer.PricePerUnit * count;
                    remaining -= count;
                    if (remaining <= 0.0)
                    {
                        break;
                    }
                }

                if (remaining > 0.0)
                {
                    return false;
                }
            }

            return needsMarketResource;
        }

        private static bool BuyMissingResourcesFromMarket(ObjectInfoData data, List<MarketPurchase> purchases)
        {
            if (data?.company == null || purchases == null)
            {
                return false;
            }

            foreach (MarketPurchase purchase in purchases)
            {
                if (purchase.Offer == null || purchase.Count <= 0.0 || !purchase.Offer.FullFill(data.company, purchase.Count))
                {
                    return false;
                }
            }

            return true;
        }

        internal static MissionPlannerDemandSummary BuildMissionPlannerDemandSummary(ObjectInfoData data, int compactLineLimit = 4)
        {
            if (data == null)
            {
                return MissionPlannerDemandSummary.Empty;
            }

            List<Facility> queued = data.ListFacility.Where(IsQueued).ToList();
            if (queued.Count == 0)
            {
                return new MissionPlannerDemandSummary("Queued construction: none", "Queued construction: none");
            }

            Dictionary<ResourceDefinition, double> demand = GetQueuedFacilityDemand(data);
            if (demand.Count == 0)
            {
                return new MissionPlannerDemandSummary("Queued construction: no resource cost", "Queued construction: no resource cost");
            }

            List<ResourceDemandLine> lines = demand
                .Select(item => new ResourceDemandLine(item.Key, GetStockpile(data, item.Key), item.Value))
                .OrderByDescending(line => line.Missing)
                .ThenByDescending(line => line.Needed)
                .ToList();

            string compact = BuildDemandText(lines.Take(compactLineLimit).ToList(), queued.Count, lines.Count > compactLineLimit ? lines.Count - compactLineLimit : 0);
            string full = BuildDemandText(lines, queued.Count, 0);
            return new MissionPlannerDemandSummary(compact, full);
        }

        internal static Dictionary<ResourceDefinition, double> GetQueuedFacilityDemand(ObjectInfoData data)
        {
            Dictionary<ResourceDefinition, double> demand = new Dictionary<ResourceDefinition, double>();
            if (data == null)
            {
                return demand;
            }

            foreach (Facility facility in data.ListFacility.Where(IsQueued))
            {
                if (facility.facilityDescriptor == null)
                {
                    continue;
                }
                ResourcePrice price = GetPrice(facility.facilityDescriptor, data.ObjectInfo);
                foreach (ResourcePriceOne resource in price.ListResources)
                {
                    if (resource.ResourceDefinition == null || resource.Price <= 0.0)
                    {
                        continue;
                    }
                    if (!demand.ContainsKey(resource.ResourceDefinition))
                    {
                        demand[resource.ResourceDefinition] = 0.0;
                    }
                    demand[resource.ResourceDefinition] += resource.Price;
                }
            }

            return demand;
        }

        private static double GetStockpile(ObjectInfoData data, ResourceDefinition resource)
        {
            RowResourcesData row = data.ListRowResourcesData.FirstOrDefault(item => item.ResourcesType == resource);
            return row != null ? Math.Max(0.0, row.Value) : 0.0;
        }

        internal static bool TryGetQueuedNeed(ObjectInfoData data, ResourceDefinition resource, out double needed)
        {
            needed = 0.0;
            if (data == null || resource == null)
            {
                return false;
            }

            Dictionary<ResourceDefinition, double> demand = GetQueuedFacilityDemand(data);
            return demand.TryGetValue(resource, out needed) && needed > 0.0;
        }

        internal static string FormatResourceStockpileWithQueuedNeed(RowResourcesData rowResourcesData)
        {
            return FormatResourceStockpileWithQueuedNeed(rowResourcesData, rowResourcesData?.ObjectInfoData);
        }

        internal static string FormatResourceStockpileWithQueuedNeed(RowResourcesData rowResourcesData, ObjectInfoData objectInfoData)
        {
            if (rowResourcesData == null || rowResourcesData.ResourcesType == null || !TryGetQueuedNeed(objectInfoData, rowResourcesData.ResourcesType, out double needed))
            {
                return null;
            }

            string current = FormatResourceAmount(rowResourcesData.ResourcesType, rowResourcesData.Value);
            string totalNeeded = FormatResourceAmount(rowResourcesData.ResourcesType, needed);
            string color = rowResourcesData.Value < needed ? "#FF8A66" : "#9BE07B";
            return current + "/<color=" + color + ">" + totalNeeded + "</color>";
        }

        internal static string AddQueuedNeedToResourceTooltip(RowResourcesData rowResourcesData, string tooltip)
        {
            return AddQueuedNeedToResourceTooltip(rowResourcesData, rowResourcesData?.ObjectInfoData, tooltip);
        }

        internal static string AddQueuedNeedToResourceTooltip(RowResourcesData rowResourcesData, ObjectInfoData objectInfoData, string tooltip)
        {
            if (rowResourcesData == null || rowResourcesData.ResourcesType == null || !TryGetQueuedNeed(objectInfoData, rowResourcesData.ResourcesType, out double needed))
            {
                return tooltip;
            }

            double missing = Math.Max(0.0, needed - rowResourcesData.Value);
            string stock = FormatResourceAmount(rowResourcesData.ResourcesType, rowResourcesData.Value);
            string totalNeeded = FormatResourceAmount(rowResourcesData.ResourcesType, needed);
            string missingText = FormatResourceAmount(rowResourcesData.ResourcesType, missing);

            return tooltip
                + Environment.NewLine
                + Environment.NewLine
                + "<color=#FFD66B>Queued construction:</color>"
                + Environment.NewLine
                + "Stockpile / needed: "
                + stock
                + " / "
                + totalNeeded
                + Environment.NewLine
                + "Extra needed: "
                + (missing > 0.0 ? "<color=#FF8A66>" + missingText + "</color>" : "<color=#9BE07B>0</color>");
        }

        private static string FormatResourceAmount(ResourceDefinition resource, double value)
        {
            switch (resource.ResourceType)
            {
                case ResourceDefinition.EResourceType.Energy:
                    return value.ToPostfixString(Language.LEManager.Get("UI.EnergyFormat"));
                case ResourceDefinition.EResourceType.Human:
                    return value.ToPostfixString("{0}{1}", gray: true, intFormat: true);
                default:
                    return value.ToPostfixString(Language.LEManager.Get("UI.MassFormat"));
            }
        }

        private static string BuildDemandText(List<ResourceDemandLine> lines, int queuedCount, int additionalLineCount)
        {
            List<string> parts = new List<string>
            {
                "<color=#FFD66B>Queued construction needs (" + queuedCount + "):</color>"
            };

            foreach (ResourceDemandLine line in lines)
            {
                parts.Add(FormatDemandLine(line));
            }

            if (additionalLineCount > 0)
            {
                parts.Add("<color=#AAAAAA>+" + additionalLineCount + " more resources</color>");
            }

            return string.Join(Environment.NewLine, parts);
        }

        private static string FormatDemandLine(ResourceDemandLine line)
        {
            string color = line.Missing > 0.0 ? "#FF8A66" : "#9BE07B";
            return line.Resource.GetText(longText: true, firstIcon: true, addSpace: true)
                + ": "
                + line.Stockpile.ToPostfixString()
                + " / "
                + line.Needed.ToPostfixString()
                + " <color="
                + color
                + ">(missing "
                + line.Missing.ToPostfixString()
                + ")</color>";
        }

        internal static Facility QueueFacility(ObjectInfoData data, FacilityBaseDescriptor descriptor)
        {
            if (data == null || descriptor == null)
            {
                return null;
            }
            Facility facility = data.AddFacility(descriptor, prebuilt: false);
            if (facility == null)
            {
                return null;
            }
            StartBuildRef(facility) = false;
            facility.BuildProgress = 0f;
            data.MarkIsDirty();
            data.InvokeRefreshUIAddFacilityOrBuildProductItem();
            Plugin.Log.LogInfo($"Queued {descriptor.ID} on {data.ObjectInfo.ObjectName}.");
            return facility;
        }

        internal static void TryStartQueuedFacilities(ObjectInfoData data)
        {
            if (data == null || data.company == null || !data.company.IsPlayer)
            {
                return;
            }
            List<Facility> queued = data.ListFacility.Where(IsQueued).ToList();
            foreach (Facility facility in queued)
            {
                if (facility.facilityDescriptor == null)
                {
                    continue;
                }
                ResourcePrice price = GetPrice(facility.facilityDescriptor, data.ObjectInfo);
                if (!CanAfford(data, price))
                {
                    continue;
                }
                if (!data.RemoveResource(price))
                {
                    continue;
                }
                facility.StartBuilding();
                data.MarkIsDirty();
                data.InvokeRefreshUIAddFacilityOrBuildProductItem();
                Plugin.Log.LogInfo($"Started queued {facility.facilityDescriptor.ID} on {data.ObjectInfo.ObjectName}.");
            }
        }

        internal static string GetQueuedTooltip(Facility facility)
        {
            if (facility == null || facility.ObjectInfoData == null || facility.facilityDescriptor == null)
            {
                return QueuedTooltipHeader;
            }
            ResourcePrice price = GetPrice(facility.facilityDescriptor, facility.ObjectInfoData.ObjectInfo);
            string resources = BuildPresentNeededString(facility.ObjectInfoData, price);
            return QueuedTooltipHeader + Environment.NewLine + BuildStackSummary(facility) + Environment.NewLine + facility.Name + Environment.NewLine + resources;
        }

        internal static string AddStackSummaryToTooltip(Facility facility, string tooltip)
        {
            if (GetDisplayStackCount(facility) <= 1)
            {
                return tooltip;
            }
            return BuildStackSummary(facility) + Environment.NewLine + tooltip;
        }

        private static string BuildStackSummary(Facility facility)
        {
            int total = GetDisplayStackCount(facility);
            int queued = GetDisplayQueuedCount(facility);
            int building = GetDisplayBuildingCount(facility);
            if (total <= 1)
            {
                return "";
            }
            List<string> parts = new List<string>();
            if (building > 0)
            {
                parts.Add(building + " building");
            }
            if (queued > 0)
            {
                parts.Add(queued + " queued");
            }
            return "<color=#FFD66B>Stack: " + total + " (" + string.Join(", ", parts) + ")</color>";
        }

        private static string BuildPresentNeededString(ObjectInfoData data, ResourcePrice price)
        {
            List<string> parts = new List<string>();
            foreach (ResourcePriceOne resource in price.ListResources)
            {
                double present = Math.Max(0.0, data.CheckResources(resource.ResourceDefinition));
                string presentText = Math.Min(present, resource.Price).ToPostfixString();
                string neededText = resource.Price.ToPostfixString();
                parts.Add(resource.ResourceDefinition.GetText() + ": " + presentText + " / " + neededText);
            }
            if (price.BuildCost > 0.0)
            {
                parts.Add("Money: " + data.company.MoneyController.CurrentMoney.ToPostfixString() + " / " + price.BuildCost.ToPostfixString());
            }
            return string.Join(Environment.NewLine, parts);
        }

        internal static void MarkRowStack(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility row, Facility facility)
        {
            TMP_Text textCount = Traverse.Create(row).Field("textCount").GetValue<TMP_Text>();
            if (textCount == null)
            {
                return;
            }
            int count = GetDisplayStackCount(facility);
            if (count > 1)
            {
                textCount.text = count.ToString();
            }
            else if (IsQueued(facility))
            {
                textCount.text = "Q";
            }
            else
            {
                return;
            }
            if (textCount.transform != null && textCount.transform.parent != null)
            {
                textCount.transform.parent.gameObject.SetActive(true);
            }
        }

        internal struct MissionPlannerDemandSummary
        {
            internal static readonly MissionPlannerDemandSummary Empty = new MissionPlannerDemandSummary("", "");

            internal readonly string CompactText;
            internal readonly string FullText;

            internal MissionPlannerDemandSummary(string compactText, string fullText)
            {
                CompactText = compactText;
                FullText = fullText;
            }
        }

        private struct ResourceDemandLine
        {
            internal readonly ResourceDefinition Resource;
            internal readonly double Stockpile;
            internal readonly double Needed;

            internal double Missing => Math.Max(0.0, Needed - Stockpile);

            internal ResourceDemandLine(ResourceDefinition resource, double stockpile, double needed)
            {
                Resource = resource;
                Stockpile = stockpile;
                Needed = needed;
            }
        }

        private struct MarketPurchase
        {
            internal readonly Offer Offer;
            internal readonly double Count;

            internal MarketPurchase(Offer offer, double count)
            {
                Offer = offer;
                Count = count;
            }
        }
    }
}
