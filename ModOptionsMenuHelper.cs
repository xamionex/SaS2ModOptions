using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectMage.player;
using ProjectMage.player.menu;

namespace SaS2ModOptions;

public static class ModOptionsMenuHelper
{
    private static FieldInfo _levelField;

    /// Helper to inject custom screen into PlayerMenu's level list
    public static void AddAndActivateScreen(Player player, LevelModOptions screen)
    {
        _levelField ??= AccessTools.Field(typeof(PlayerMenu), "level");
        if (_levelField == null)
        {
            SaS2ModOptions.Instance.Log.LogError("PlayerMenu.level field not found.");
            return;
        }

        if (_levelField.GetValue(player.menu) is not List<LevelBase> levelList) return;

        // Remove any existing inactive instance to prevent conflicts
        levelList.RemoveAll(l => l is LevelModOptions && !l.IsActive());

        // Look for existing active instance
        var existing = levelList.OfType<LevelModOptions>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        levelList.Add(screen);
        screen.Activate();
    }
}