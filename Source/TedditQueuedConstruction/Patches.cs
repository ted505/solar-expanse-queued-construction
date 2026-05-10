using System.Collections.Generic;
using System.Linq;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.UI.Windows.Elements;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using TMPro;

namespace TedditQueuedConstruction
{
    [HarmonyPatch(typeof(ObjectInfoWindow), "FacilityListOnOnClickCreateFacilityPart1")]
    internal static class ObjectInfoWindowFacilityCreatePatch
    {
        private static bool Prefix(ObjectInfoWindow __instance, FacilityBaseDescriptor obj, ObjectInfo objectInfo, bool showPopUP, ref bool __result)
        {
            if (obj == null)
            {
                return true;
            }

            ObjectInfo target = __instance.ObjectInfoCurrent ?? objectInfo;
            if (target == null)
            {
                return true;
            }

            ObjectInfoData data = target.GetObjectInfoData(MonoBehaviourSingleton<GameManager>.Instance.Player);
            if (data == null)
            {
                return true;
            }

            ResourcePrice price = FacilityQueue.GetPrice(obj, target);
            if (data.CanAddFacility(obj) && !FacilityQueue.CanAfford(data, price))
            {
                if (FacilityQueue.TryShowMarketPurchasePopup(data, obj, price))
                {
                    // In the stock shift-click loop, false stops the loop. If buying is available,
                    // process only this one construction so the player gets one clear choice.
                    __result = showPopUP;
                    return false;
                }

                FacilityQueue.QueueFacility(data, obj);
                __result = true;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ObjectInfoData), "MyUpdate")]
    internal static class ObjectInfoDataMyUpdatePatch
    {
        private static void Postfix(ObjectInfoData __instance)
        {
            FacilityQueue.TryStartQueuedFacilities(__instance);
        }
    }

    [HarmonyPatch(typeof(ObjectInfoData), "PreUpdateBuilding")]
    internal static class ObjectInfoDataPreUpdateBuildingPatch
    {
        private static bool Prefix(ObjectInfoData __instance)
        {
            List<ProductionItem> productionItemBuildingModule = Traverse.Create(__instance).Field("productionItemBuildingModule").GetValue<List<ProductionItem>>();
            if (productionItemBuildingModule == null)
            {
                productionItemBuildingModule = new List<ProductionItem>();
                Traverse.Create(__instance).Field("productionItemBuildingModule").SetValue(productionItemBuildingModule);
            }
            productionItemBuildingModule.Clear();

            List<ProductionItem> productionItemSCLV = Traverse.Create(__instance).Field("productionItemSCLV").GetValue<List<ProductionItem>>();
            if (productionItemSCLV == null)
            {
                productionItemSCLV = new List<ProductionItem>();
                Traverse.Create(__instance).Field("productionItemSCLV").SetValue(productionItemSCLV);
            }
            productionItemSCLV.Clear();

            int facilitySlotsUsed = 0;
            int spacecraftSlotsUsed = 0;
            double vehicleAssemblyCapacity = __instance.VehicleAssemblyCountEnable;
            if (vehicleAssemblyCapacity > 0.0 && vehicleAssemblyCapacity < 1.0)
            {
                vehicleAssemblyCapacity = 1.0;
            }

            foreach (ProductionItem item in __instance.ProductionItem)
            {
                if (item == null || item.BuildProgress >= 1f || !item.StartBuild)
                {
                    continue;
                }

                if (item.ProductionItemType is FacilityBaseDescriptor || item.ProductionItemType is SpaceModuleDescriptor)
                {
                    bool requiresConstructionEquipment = true;
                    FacilityBaseDescriptor descriptor = item.ProductionItemType as FacilityBaseDescriptor;
                    if (descriptor != null)
                    {
                        requiresConstructionEquipment = descriptor.ConstructionEquipmentCountIsRequired;
                    }

                    if (requiresConstructionEquipment)
                    {
                        facilitySlotsUsed++;
                        if (facilitySlotsUsed <= __instance.ConstructionEquipmentCount)
                        {
                            productionItemBuildingModule.Add(item);
                        }
                    }
                    else
                    {
                        productionItemBuildingModule.Add(item);
                    }
                }
                else if (item.ProductionItemType is SpacecraftType || item.ProductionItemType is LaunchVehicleType)
                {
                    spacecraftSlotsUsed++;
                    if ((double)spacecraftSlotsUsed <= vehicleAssemblyCapacity)
                    {
                        productionItemSCLV.Add(item);
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("Unknown production item type in PreUpdateBuilding: " + item.ProductionItemType);
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(Facility), "CancelBuild")]
    internal static class FacilityCancelBuildPatch
    {
        private static bool Prefix(Facility __instance)
        {
            if (!FacilityQueue.IsQueued(__instance))
            {
                return true;
            }

            ObjectInfoData data = __instance.ObjectInfoData;
            if (data == null)
            {
                return true;
            }

            data.RemoveProductionItem(__instance);
            data.MarkIsDirty();
            data.InvokeRefreshUIAddFacilityOrBuildProductItem();
            Plugin.Log.LogInfo($"Canceled queued {__instance.facilityDescriptor?.ID} on {data.ObjectInfo.ObjectName}.");
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfoWindow), "RefreshFacilityList")]
    internal static class ObjectInfoWindowRefreshFacilityListPatch
    {
        private static bool Prefix(ObjectInfoWindow __instance)
        {
            ObjectInfoData data = __instance.ObjectInfoDataCurrent;
            if (data == null)
            {
                return true;
            }

            List<Facility> rows = FacilityQueue.BuildFacilityPanelRows(data);

            Traverse.Create(__instance).Field("facilityList").GetValue<Game.UI.Windows.Elements.ObjectInfoElements.UIFacilityList>().SetData(rows);
            return false;
        }
    }

    [HarmonyPatch(typeof(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility), "SetData")]
    internal static class ObjectInfoFacilityRowSetDataPatch
    {
        private static void Postfix(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility __instance)
        {
            FacilityQueue.MarkRowStack(__instance, __instance.Facility);
        }
    }

    [HarmonyPatch(typeof(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility), "GetTooltipString")]
    internal static class ObjectInfoFacilityRowTooltipPatch
    {
        private static bool Prefix(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility __instance, ref string __result)
        {
            if (!FacilityQueue.IsQueued(__instance.Facility))
            {
                return true;
            }

            __result = FacilityQueue.GetQueuedTooltip(__instance.Facility);
            return false;
        }
    }

    [HarmonyPatch(typeof(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility), "GetTooltipString")]
    internal static class ObjectInfoFacilityRowTooltipStackPatch
    {
        private static void Postfix(Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility __instance, ref string __result)
        {
            if (FacilityQueue.IsQueued(__instance.Facility))
            {
                return;
            }

            __result = FacilityQueue.AddStackSummaryToTooltip(__instance.Facility, __result);
        }
    }

    [HarmonyPatch(typeof(UIRowResources), "SetData")]
    internal static class ObjectInfoResourceRowSetDataPatch
    {
        private static void Postfix(UIRowResources __instance)
        {
            ObjectInfoData objectInfoData = __instance.ResourcesData?.ObjectInfoData;
            if (objectInfoData == null)
            {
                ObjectInfoWindow objectInfoWindow = Traverse.Create((ListElement)__instance).Field("parentWindow").GetValue<UIWindow>() as ObjectInfoWindow;
                objectInfoData = objectInfoWindow?.ObjectInfoDataCurrent;
            }

            string stockpileWithNeed = FacilityQueue.FormatResourceStockpileWithQueuedNeed(__instance.ResourcesData, objectInfoData);
            if (string.IsNullOrEmpty(stockpileWithNeed))
            {
                return;
            }

            TMP_Text valueText = Traverse.Create(__instance).Field("resourcesValueTextMeshPro").GetValue<TMP_Text>();
            if (valueText != null)
            {
                valueText.text = stockpileWithNeed;
            }
            if (!__instance.gameObject.activeSelf)
            {
                __instance.gameObject.SetActive(true);
            }
        }
    }

    [HarmonyPatch(typeof(UIRowResources), "GetTooltipStringStatic")]
    internal static class ObjectInfoResourceRowTooltipPatch
    {
        private static void Postfix(RowResourcesData rowResourcesData, UIWindow parentWindow, ref string __result)
        {
            ObjectInfoData objectInfoData = rowResourcesData?.ObjectInfoData;
            if (objectInfoData == null && parentWindow is ObjectInfoWindow objectInfoWindow)
            {
                objectInfoData = objectInfoWindow.ObjectInfoDataCurrent;
            }

            __result = FacilityQueue.AddQueuedNeedToResourceTooltip(rowResourcesData, objectInfoData, __result);
        }
    }
}
