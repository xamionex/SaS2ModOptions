using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.player.menu.levels;

namespace SaS2ModOptions;

[HarmonyPatch]
public static class LevelGameMenuPatch
{
    /// Icon ID for the Mod Options entry (110 = ICON_GAMEPAD)
    private const int ModOptionsId = 110;
    private static readonly MethodInfo CreateListMethod = AccessTools.Method(typeof(LevelMenuListBase), "CreateList");

    /// Patch Update to open our screen when "Mod Options" is selected
    [HarmonyPatch(typeof(LevelGameMenu), "Update")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    public static void LevelGameMenu_Postfix(LevelGameMenu __instance, Character character, float frameTime)
    {
        if (!__instance.CanInput() || !__instance.player.keys.keyAccept)
            return;

        var selY = __instance.selY;
        var icons = __instance.icons;
        if (icons == null || selY < 0 || selY >= icons.Count) return;

        if (icons[selY] != ModOptionsId) return;

        __instance.PlayAccept();
        __instance.Deactivate();

        var modOptionsScreen = new LevelModOptions(__instance.player, returnToScreen: 10);
        ModOptionsMenuHelper.AddAndActivateScreen(__instance.player, modOptionsScreen);
    }

    /// Transpiler: Add "Mod Options" to the dictionary before CreateList
    [HarmonyPatch(typeof(LevelGameMenu), "Init")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> LevelGameMenu_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        SaS2ModOptions.Instance.Log.LogInfo("LevelGameMenu transpiler running...");
        var codes = instructions.ToList();
        var callIndex = codes.FindIndex(ci => ci.Calls(CreateListMethod));
        if (callIndex == -1)
        {
            SaS2ModOptions.Instance.Log.LogWarning("CreateList call not found in LevelGameMenu – Mod Options entry won't be added.");
            return codes;
        }

        // Insert before the call: dictionary.Add(ModOptionsId, "Mod Options");
        var newCodes = new List<CodeInstruction>();
        for (var i = 0; i < codes.Count; i++)
        {
            newCodes.Add(codes[i]);
            if (i != callIndex - 1) continue;

            // just before the call, the dictionary is on stack
            newCodes.Add(new CodeInstruction(OpCodes.Dup));
            newCodes.Add(new CodeInstruction(OpCodes.Ldc_I4, ModOptionsId));
            newCodes.Add(new CodeInstruction(OpCodes.Ldstr, "Mod Options"));
            newCodes.Add(CodeInstruction.Call(typeof(Dictionary<int, string>), "Add", [typeof(int), typeof(string)]));
        }
        SaS2ModOptions.Instance.Log.LogInfo("LevelGameMenu transpiler completed successfully.");
        return newCodes;
    }
}