using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.player.menu.levels;

namespace SaS2ModOptions;

[HarmonyPatch]
public static class LevelMainMenuPatch
{
    /// Icon ID for the Mod Options entry (110 = ICON_GAMEPAD)
    private const int ModOptionsId = 110;
    private static readonly MethodInfo CreateListMethod = AccessTools.Method(typeof(LevelMenuListBase), "CreateList");

    /// Patch LevelMainMenu.Update to open our screen
    [HarmonyPatch(typeof(LevelMainMenu), "Update")]
    [HarmonyPostfix]
    public static void LevelMainMenuPostFix(LevelMainMenu __instance, Character character, float frameTime)
    {
        if (!__instance.CanInput() || !__instance.player.keys.keyAccept)
            return;

        var selY = __instance.selY;
        var icons = __instance.icons;
        if (icons == null || selY < 0 || selY >= icons.Count) return;

        if (icons[selY] != ModOptionsId) return;

        __instance.PlayAccept();
        __instance.Deactivate();

        var modOptionsScreen = new LevelModOptions(__instance.player, returnToScreen: 25); // 25 = LevelMainMenu
        ModOptionsMenuHelper.AddAndActivateScreen(__instance.player, modOptionsScreen);
    }

    /// Transpiler: Add "Mod Options" to LevelMainMenu dictionary
    [HarmonyPatch(typeof(LevelMainMenu), "Init")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        SaS2ModOptions.Instance.Log.LogInfo("LevelMainMenu transpiler running...");
        var codes = instructions.ToList();
        var callIndex = codes.FindIndex(ci => ci.Calls(CreateListMethod));
        if (callIndex == -1)
        {
            SaS2ModOptions.Instance.Log.LogWarning("CreateList call not found in LevelMainMenu – Mod Options entry won't be added.");
            return codes;
        }

        var newCodes = new List<CodeInstruction>();
        for (var i = 0; i < codes.Count; i++)
        {
            newCodes.Add(codes[i]);
            if (i == callIndex - 1)
            {
                newCodes.Add(new CodeInstruction(OpCodes.Dup));
                newCodes.Add(new CodeInstruction(OpCodes.Ldc_I4, ModOptionsId));
                newCodes.Add(new CodeInstruction(OpCodes.Ldstr, "Mod Options"));
                newCodes.Add(CodeInstruction.Call(typeof(Dictionary<int, string>), "Add", [typeof(int), typeof(string)]));
            }
        }
        SaS2ModOptions.Instance.Log.LogInfo("LevelMainMenu transpiler completed successfully.");
        return newCodes;
    }
}
