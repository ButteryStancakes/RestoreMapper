using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RestoreMapper
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.restoremapper", PLUGIN_NAME = "Restore Mapper", PLUGIN_VERSION = "1.0.0";
        internal static new ManualLogSource Logger;

        void Awake()
        {
            Logger = base.Logger;

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class RestoreMapperPatches
    {
        [HarmonyPatch(typeof(Terminal), "Awake")]
        [HarmonyPostfix]
        static void TerminalPostAwake(Terminal __instance)
        {
            TerminalKeyword buy = __instance.terminalNodes?.allKeywords.FirstOrDefault(keyword => keyword.name == "Buy");
            TerminalNode buyMapper = buy?.compatibleNouns.FirstOrDefault(compatibleNoun => compatibleNoun.noun?.name == "Mapper").result;
            CompatibleNoun confirm = buyMapper?.terminalOptions.FirstOrDefault(compatibleNoun => compatibleNoun.noun?.name == "Confirm");
            List<Item> itemsList = StartOfRound.Instance?.allItemsList?.itemsList;
            Item mapperTool = itemsList?.FirstOrDefault(item => item.name == "MapDevice");

            if (buy == null || buyMapper == null || confirm == null || itemsList == null || mapperTool == null)
            {
                Plugin.Logger.LogError("Encountered an error while caching essential references. Loading will be skipped.");
                return;
            }

            // add the mapper to the list of shop items
            __instance.buyableItemsList = __instance.buyableItemsList.AddItem(mapperTool).ToArray();
            buyMapper.buyItemIndex = __instance.buyableItemsList.Length - 1;
            confirm.result.buyItemIndex = buyMapper.buyItemIndex;
            Plugin.Logger.LogDebug("Assigned IDs");

            // fix the price to match
            mapperTool.creditsWorth = 150;
            Plugin.Logger.LogDebug("Assigned price");

            // add missing SFX
            Item proFlashlight = itemsList.FirstOrDefault(item => item.name == "ProFlashlight");
            if (proFlashlight != null)
            {
                mapperTool.grabSFX = proFlashlight.grabSFX;
                mapperTool.dropSFX = proFlashlight.dropSFX;
                mapperTool.pocketSFX = proFlashlight.pocketSFX;
                Plugin.Logger.LogDebug("Assigned SFX");
            }
            else
                Plugin.Logger.LogWarning("Couldn't find pro-flashlight item. Sound effects will be missing.");

            // inventory icon
            try
            {
                AssetBundle mapperBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "restoremapper"));
                mapperTool.itemIcon = mapperBundle.LoadAsset<Sprite>("MapperIcon");
                mapperBundle.Unload(false);
                Plugin.Logger.LogDebug("Assigned special icon");
            }
            catch
            {
                Plugin.Logger.LogWarning("Encountered some error loading asset bundle. Inventory icon will be incorrect.");
                Item cardboardBox = itemsList?.FirstOrDefault(item => item.itemIcon?.name == "caticontest");
                if (cardboardBox != null)
                    mapperTool.itemIcon = cardboardBox.itemIcon;
            }
        }
    }
}