using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Chronicler.dialog;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.player;
using ProjectMage.player.menu;

namespace SaS2ModOptions;

public class LevelModOptions : LevelBase
{
    private List<SaS2ModOptions.RegisteredConfig> _displayedConfigs;
    private int _selectedIndex;
    private float _scrollOffset;
    private int _returnScreen;
    
    // UI Constants
    private const float ItemHeight = 40f;
    private const float SectionHeight = 60f;
    private float _listX;
    private float _listY;
    private float _listWidth;
    private float _valueWidth = 240f; // Widened to fit RGBA strings

    // Color Editing State
    private int _colorCompIndex = -1; // -1 = none, 0=R, 1=G, 2=B, 3=A

    // 10 = LevelGameMenu, 25 = LevelMainMenu
    public LevelModOptions(Player player, int returnToScreen = 10)
    {
        this.player = player;
        _returnScreen = returnToScreen;
        Init("ModOptions", player);
    }

    public sealed override void Init(string strScreen, Player plr)
    {
        base.Init(strScreen, plr);
        // Make this screen modal to block game input
        if (!screen.uiFlag.Contains(9)) screen.uiFlag.Add(9);
        
        _displayedConfigs = SaS2ModOptions.RegisteredConfigs
            .OrderBy(c => c.ModName)
            .ThenBy(c => c.DisplayName)
            .ToList();
    }

    // Helper to replace missing Math.Clamp in .NET Framework 4.5
    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

    public override void Update(Character character, float frameTime)
    {
        if (!CanInput()) return;

        if (player.keys.keyUp || player.keys.keyDown)
        {
            var dir = player.keys.keyUp ? -1 : 1;
            _selectedIndex = (_selectedIndex + dir + _displayedConfigs.Count) % _displayedConfigs.Count;
            _colorCompIndex = -1; // Reset color editing when moving
            PlaySelect();
            EnsureVisible();
            return;
        }

        var config = _displayedConfigs[_selectedIndex];
        var isColor = IsColorString(config.Entry);
        var valueChanged = false;

        // Color Picker: Press Accept to cycle R -> G -> B -> A -> Off
        if (player.keys.keyAccept && isColor)
        {
            _colorCompIndex++;
            if (_colorCompIndex > 3) _colorCompIndex = -1;
            PlayAccept();
            return;
        }

        if (player.keys.keyLeft || player.keys.keyRight)
        {
            var right = player.keys.keyRight;
            if (isColor && _colorCompIndex != -1)
            {
                ModifyColorComponent(config, _colorCompIndex, right);
            }
            else
            {
                ModifyValue(config, right);
            }

            valueChanged = true;
        }
        else if (player.keys.keyAccept && !isColor)
        {
            ModifyValue(config, true);
            valueChanged = true;
        }

        if (valueChanged)
        {
            config.Entry.ConfigFile.Save();
            PlaySelect();
        }

        if (player.keys.keyCancel)
        {
            PlayCancel();
            Deactivate();
            player.menu.GetLevelByScreen(_returnScreen).Activate();
        }
    }

    private void ModifyValue(SaS2ModOptions.RegisteredConfig config, bool increase)
    {
        var type = config.Entry.SettingType;
        if (type == typeof(bool))
            ((ConfigEntry<bool>)config.Entry).Value = !((ConfigEntry<bool>)config.Entry).Value;
        else if (type.IsEnum)
            CycleEnum(config, increase);
        else if (type == typeof(int))
            ((ConfigEntry<int>)config.Entry).Value += increase ? 1 : -1;
        else if (type == typeof(float))
            ((ConfigEntry<float>)config.Entry).Value += increase ? 0.05f : -0.05f;
    }

    private void ModifyColorComponent(SaS2ModOptions.RegisteredConfig config, int comp, bool inc)
    {
        var parts = ((string)config.Entry.BoxedValue).Split(',');
        if (parts.Length != 4) return;

        if (comp < 3) // RGB Channels (0-255)
        {
            if (int.TryParse(parts[comp], out var v))
            {
                v = Clamp(inc ? v + 5 : v - 5, 0, 255);
                parts[comp] = v.ToString();
            }
        }
        else // Alpha Channel (0.0-1.0)
        {
            if (float.TryParse(parts[comp], out var v))
            {
                v = (float)Math.Round(Clamp(inc ? v + 0.05f : v - 0.05f, 0f, 1f), 2);
                parts[comp] = v.ToString();
            }
        }
        config.Entry.BoxedValue = string.Join(",", parts);
    }

    private static void CycleEnum(SaS2ModOptions.RegisteredConfig config, bool forward)
    {
        var values = Enum.GetValues(config.Entry.SettingType);
        var index = Array.IndexOf(values, config.Entry.BoxedValue);
        index = forward ? (index + 1) % values.Length : (index - 1 + values.Length) % values.Length;
        config.Entry.BoxedValue = values.GetValue(index);
    }

    private bool IsColorString(ConfigEntryBase entry) => 
        entry.SettingType == typeof(string) && ((string)entry.BoxedValue)?.Split(',').Length == 4;

    public override void Draw()
    {
        base.Draw();
        var vp = Game1.Instance.GraphicsDevice.Viewport;
        var boxWidth = Math.Min(1000, vp.Width * 0.8f);
        var boxHeight = vp.Height * 0.7f;
        var boxX = (vp.Width - boxWidth) / 2f;
        var boxY = (vp.Height - boxHeight) / 2f;

        UIRender.DrawRect(new Rectangle((int)boxX, (int)boxY, (int)boxWidth, (int)boxHeight), 0.85f, 0, 1f, 1f, UIRender.interfaceTex);

        // These are computed dynamically based on the box position
        _listX = boxX + 40f;
        _listY = boxY + 40f;
        _listWidth = boxWidth - 80f;
        var listVisibleHeight = boxHeight - 80f;
        var currentY = _listY - _scrollOffset;

        string lastMod = null;
        for (var i = 0; i < _displayedConfigs.Count; i++)
        {
            var cfg = _displayedConfigs[i];
            var selected = i == _selectedIndex;

            // Render Mod Section Title
            if (cfg.ModName != lastMod)
            {
                if (lastMod != null) currentY += 20f;
                if (currentY + SectionHeight > _listY && currentY < _listY + listVisibleHeight)
                {
                    // Layout Fix: Added +35f vertical offset to center mod title correctly
                    Text.DrawText(new StringBuilder(cfg.ModName), new Vector2(_listX, currentY + 35f), 
                        new Color(0.6f, 0.8f, 1f, 1f), 0.85f, 0);
                    UIRender.DrawDivider(new Vector2(_listX + _listWidth / 2f, currentY + 45f), 0.7f, 1f, 1f, 0.7f, 0.5f, 1, UIRender.interfaceTex);
                }
                currentY += SectionHeight;
                lastMod = cfg.ModName;
            }

            // Render Config Item
            if (currentY + ItemHeight > _listY && currentY < _listY + listVisibleHeight)
            {
                if (selected)
                    UIRender.DrawRect(new Rectangle((int)_listX, (int)currentY, (int)_listWidth, (int)ItemHeight), 0.2f, 3, 1f, 1f, UIRender.interfaceTex);

                var textColor = selected ? Color.Yellow : Color.White;
                var textY = currentY + ItemHeight * 0.75f;

                Text.DrawText(new StringBuilder(cfg.DisplayName), new Vector2(_listX + 10, textY), textColor, 0.7f, 0);
                
                var valStr = FormatValue(cfg.Entry, selected);
                Text.DrawText(new StringBuilder(valStr), new Vector2(_listX + _listWidth - _valueWidth, textY), textColor, 0.7f, 0);
            }
            currentY += ItemHeight;
        }

        var action = player.inputProfile.keyMouseEnable ? "[Space]" : "[a]";
        var help = new StringBuilder($"\u02ef{action}\u02f0 Cycle/Edit  |  \u02ef[ll]/[lr]\u02f0 Change  |  \u02ef[b]\u02f0 Back");
        Text.DrawText(help, new Vector2(vp.Width / 2, vp.Height - 40), Color.White, 0.6f, 1, player, 1);
    }

    private string FormatValue(ConfigEntryBase entry, bool selected)
    {
        if (IsColorString(entry) && selected && _colorCompIndex != -1)
        {
            var p = ((string)entry.BoxedValue).Split(',');
            p[_colorCompIndex] = ">" + p[_colorCompIndex] + "<";
            return string.Join(",", p);
        }
        if (entry.SettingType == typeof(bool)) return ((ConfigEntry<bool>)entry).Value ? "On" : "Off";
        if (entry.SettingType == typeof(float)) return ((ConfigEntry<float>)entry).Value.ToString("F2");
        return entry.BoxedValue?.ToString() ?? "null";
    }

    private void EnsureVisible()
    {
        var y = GetItemY(_selectedIndex);
        if (y < _scrollOffset) _scrollOffset = y;
        else if (y + ItemHeight > _scrollOffset + (Game1.Instance.GraphicsDevice.Viewport.Height * 0.7f - 80f))
            _scrollOffset = y + ItemHeight - (Game1.Instance.GraphicsDevice.Viewport.Height * 0.7f - 80f);
    }

    private float GetItemY(int index)
    {
        float y = 0; string last = null;
        for (var i = 0; i <= index; i++) {
            if (_displayedConfigs[i].ModName != last) { y += last == null ? SectionHeight : SectionHeight + 20f; last = _displayedConfigs[i].ModName; }
            if (i < index) y += ItemHeight;
        }
        return y;
    }

    private new void PlaySelect() => AccessTools.Method(typeof(LevelBase), "PlaySelect")?.Invoke(this, null);
    private new void PlayAccept() => AccessTools.Method(typeof(LevelBase), "PlayAccept")?.Invoke(this, null);
    private new void PlayCancel() => AccessTools.Method(typeof(LevelBase), "PlayCancel")?.Invoke(this, null);
    private new bool CanInput() => (bool)AccessTools.Method(typeof(LevelBase), "CanInput")?.Invoke(this, null)!;
}