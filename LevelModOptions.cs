using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.gamestate;
using ProjectMage.player;
using ProjectMage.player.menu;

namespace SaS2ModOptions;

public class LevelModOptions : LevelBase
{
    // Full sorted config list, doesn't change
    private List<SaS2ModOptions.RegisteredConfig> _allConfigs;

    // Configs for the currently active tab only
    private List<SaS2ModOptions.RegisteredConfig> _displayedConfigs;

    // Tab state
    private List<string> _tabs;
    private int _currentTabIndex;

    private int _selectedIndex;
    private float _scrollOffset;
    private readonly int _returnScreen;
    private int _currentPlayerId;
    private bool _fast;

    // UI Constants
    private const float TabBarHeight = 36f; // height of one tab row
    private const float TabPadX = 18f; // horizontal text padding inside each tab
    private const float ItemHeight = 40f;
    private const float SectionHeight = 60f;
    private const float TopMargin = 40f; // space above the tab bar
    private const float BottomMargin = 40f; // space below the last item=

    private float _listX;
    private float _listY;
    private float _listWidth;
    private const float ValueWidth = 240f;

    // Color Editing State
    private int _colorCompIndex = -1; // -1 = none, 0=R, 1=G, 2=B, 3=A

    // Keybind capture state. Non-null while waiting for the user to press a key/button to rebind.
    private SaS2ModOptions.RegisteredConfig _rebindingConfig;
    private Keybind.Capture _rebindCapture;

    // Dynamic sizing
    private float _currentListVisibleHeight;

    // Mouse hit-test rects recorded during Draw() and consumed by the next Update().
    // Mouse input only applies to the player whose ID == 0 (see MouseMgr), matching vanilla.
    private struct ItemHit
    {
        public int Index;
        public Rectangle Rect;
    }

    private Rectangle[] _tabHitRects;
    private readonly List<ItemHit> _itemHitRects = [];

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
        _currentPlayerId = plr.ID; // 0 = Player1, 1 = Player2

        _allConfigs = SaS2ModOptions.RegisteredConfigs
            .OrderBy(c => c.ModName)
            .ThenBy(c => c.Order)
            .ThenBy(c => c.DisplayName)
            .ToList();

        _tabs = _allConfigs.Select(c => c.ModName).Distinct().ToList();
        _currentTabIndex = 0;

        RefreshDisplayedConfigs();
    }

    private void RefreshDisplayedConfigs()
    {
        if (_tabs.Count == 0)
        {
            _displayedConfigs = [];
            return;
        }

        var tab = _tabs[_currentTabIndex];
        _displayedConfigs = _allConfigs.Where(c => c.ModName == tab).ToList();
        _selectedIndex = 0;
        _scrollOffset = 0f;
        _colorCompIndex = -1;
        _fast = false;
    }

    private bool HasTabs => _tabs.Count > 1;

    // Helper to get the correct ConfigEntryBase for the current player
    private ConfigEntryBase GetActiveEntry(SaS2ModOptions.RegisteredConfig cfg) =>
        cfg.GetEntryForPlayer(_currentPlayerId);

    // Helper to replace missing Math.Clamp in .NET Framework 4.5
    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

    public override void Update(Character character, float frameTime)
    {
        if (!CanInput()) return;

        // While rebinding, capture the next key press and ignore all other input.
        if (_rebindingConfig != null)
        {
            HandleRebindCapture();
            return;
        }

        // Mouse navigation (player 0 only). Returns true when it consumes a discrete
        // action (tab click, value click, scroll) so keyboard handling is skipped this frame.
        // Pure hover-select updates the selection but lets keyboard input continue.
        if (HandleMouseInput()) return;

        // Tab navigation
        if (HasTabs)
        {
            if (player.keys.keyCatLeft)
            {
                _currentTabIndex = (_currentTabIndex - 1 + _tabs.Count) % _tabs.Count;
                RefreshDisplayedConfigs();
                PlaySelect();
                return;
            }

            if (player.keys.keyCatRight)
            {
                _currentTabIndex = (_currentTabIndex + 1) % _tabs.Count;
                RefreshDisplayedConfigs();
                PlaySelect();
                return;
            }
        }

        if (_displayedConfigs.Count == 0) return;

        if (player.keys.keyUp || player.keys.keyDown)
        {
            var dir = player.keys.keyUp ? -1 : 1;
            _selectedIndex = (_selectedIndex + dir + _displayedConfigs.Count) % _displayedConfigs.Count;
            _colorCompIndex = -1;
            PlaySelect();
            EnsureVisible();
            return;
        }

        var config = _displayedConfigs[_selectedIndex];
        var activeEntry = GetActiveEntry(config);
        var isColor = IsColorString(activeEntry);
        var isBool = IsBool(activeEntry);
        var isKeybind = config.IsKeybind;
        var valueChanged = false;

        // Reset to default: X-option (Backspace / controller X) works on any option type.
        if (player.keys.keyXOption)
        {
            ResetOption(config);
            PlaySelect();
            return;
        }

        // Keybind: Accept enters capture mode; Y-option (Tab / controller Y) toggles enable/disable.
        if (isKeybind)
        {
            if (player.keys.keyAccept)
            {
                BeginRebind(config);
                return;
            }

            if (player.keys.keyYOption)
            {
                config.Keybind.ToggleEnabled();
                PlaySelect();
                return;
            }
        }

        // Color Picker: Accept cycles R -> G -> B -> A -> Off
        if (player.keys.keyAccept && isColor)
        {
            _colorCompIndex++;
            if (_colorCompIndex > 3) _colorCompIndex = -1;
            PlayAccept();
            return;
        }

        if ((player.keys.keyLeft || player.keys.keyRight || isBool && player.keys.keyAccept) && !isKeybind)
        {
            var right = player.keys.keyRight;
            if (isColor && _colorCompIndex != -1)
                ModifyColorComponent(config, _colorCompIndex, right);
            else
                ModifyValue(config, right, _fast);

            valueChanged = true;
        }
        else if (player.keys.keyAccept && !isColor && !isKeybind)
        {
            _fast = !_fast;
        }

        if (valueChanged)
        {
            GetActiveEntry(config).ConfigFile.Save();
            PlaySelect();
        }

        if (player.keys.keyCancel)
        {
            PlayCancel();
            Deactivate();
            player.menu.GetLevelByScreen(_returnScreen).Activate();
        }
    }

    // Mouse navigation for player 0. Reads MouseMgr public state and the hit-test rects
    // recorded during the previous Draw(). Mirrors vanilla CheckMouseHover: hover only
    // re-selects while the cursor is actually moving (MouseMgr.moveFrame > 0), so it never
    // fights keyboard/controller navigation when the cursor is at rest.
    // Returns true when a discrete action (tab/value click or scroll) was performed.
    private bool HandleMouseInput()
    {
        // The mouse belongs to the keyboard player (ID 0) only, matching MouseMgr.
        if (player.ID != 0 || !MouseMgr.isActive) return false;

        var moved = MouseMgr.moveFrame > 0f;
        var clickActive = GameStateManager.activeFocus;
        var leftClick = clickActive && MouseMgr.isLeftClick;
        var rightClick = clickActive && MouseMgr.isRightClick;

        // Scroll wheel moves the selection, like up/down.
        if ((MouseMgr.isScrollUp || MouseMgr.isScrollDown) && _displayedConfigs.Count > 0)
        {
            var dir = MouseMgr.isScrollUp ? -1 : 1;
            _selectedIndex = (_selectedIndex + dir + _displayedConfigs.Count) % _displayedConfigs.Count;
            _colorCompIndex = -1;
            PlaySelect();
            EnsureVisible();
            return true;
        }

        var mouse = MouseMgr.mLoc;

        // Tab clicks switch tabs.
        if (HasTabs && leftClick && _tabHitRects != null)
        {
            for (var t = 0; t < _tabHitRects.Length && t < _tabs.Count; t++)
            {
                if (!PointInRect(mouse, _tabHitRects[t])) continue;
                player.menu.mouseInRect = true;
                if (t != _currentTabIndex)
                {
                    _currentTabIndex = t;
                    RefreshDisplayedConfigs();
                    PlaySelect();
                }

                return true;
            }
        }

        if (_displayedConfigs.Count == 0) return false;

        // Item hover / click.
        foreach (var hit in _itemHitRects)
        {
            if (!PointInRect(mouse, hit.Rect)) continue;

            // Resting cursor over the list: do nothing, let keyboard/controller drive.
            if (!moved && !leftClick && !rightClick) return false;

            player.menu.mouseInRect = true;

            // Moving the cursor selects the row underneath it.
            if (moved && hit.Index != _selectedIndex)
            {
                _selectedIndex = hit.Index;
                _colorCompIndex = -1;
                PlaySelect();
            }

            if (!leftClick && !rightClick) return false; // hover only, no value change

            // A click acts on the clicked row: left = increase, right = decrease.
            if (hit.Index != _selectedIndex)
            {
                _selectedIndex = hit.Index;
                _colorCompIndex = -1;
            }

            var config = _displayedConfigs[_selectedIndex];
            var entry = GetActiveEntry(config);

            // Keybind: left click enters capture mode; right click resets to default.
            if (config.IsKeybind)
            {
                if (leftClick) BeginRebind(config);
                else if (rightClick)
                {
                    config.Keybind.ResetToDefault();
                    PlaySelect();
                }

                return true;
            }

            // Colour picker: when a component is active (entered via Accept), click nudges it;
            // otherwise behave like a normal value change.
            if (IsColorString(entry) && _colorCompIndex != -1)
                ModifyColorComponent(config, _colorCompIndex, leftClick);
            else
                ModifyValue(config, leftClick, _fast);

            GetActiveEntry(config).ConfigFile.Save();
            PlaySelect();
            return true;
        }

        return false;
    }

    private static bool PointInRect(Vector2 p, Rectangle r) =>
        p.X > r.X && p.X < r.Right && p.Y > r.Y && p.Y < r.Bottom;

    // The gamepad assigned to this menu's player (falls back to pad 0 for keyboard players),
    // used when capturing controller combos.
    private GamePadState GetPlayerGamePad()
    {
        var idx = player.inputProfile?.gamepadIdx ?? -1;
        if (idx < 0) idx = 0;
        var gps = GlobalInputMgr.gps;
        return gps != null && idx < gps.Length ? gps[idx] : default;
    }

    // Reset an option to its registered default. Keybinds restore their default combo (and
    // re-enable); every other option restores the config entry's default value.
    private void ResetOption(SaS2ModOptions.RegisteredConfig config)
    {
        if (config.IsKeybind)
        {
            config.Keybind.ResetToDefault();
            return;
        }

        var entry = GetActiveEntry(config);
        entry.BoxedValue = config.GlobalEntry.DefaultValue;
        entry.ConfigFile.Save();
        _colorCompIndex = -1;
    }

    // Enter keybind capture mode. Inputs held right now (e.g. the Accept key/button) are ignored
    // until released; the combo commits only once all capture inputs are released.
    private void BeginRebind(SaS2ModOptions.RegisteredConfig config)
    {
        _rebindingConfig = config;
        _rebindCapture = new Keybind.Capture(GlobalInputMgr.ks, GetPlayerGamePad());
        PlayAccept();
    }

    // Drive the capture each frame; commit on release. Escape cancels.
    private void HandleRebindCapture()
    {
        var ks = GlobalInputMgr.ks;
        if (ks.IsKeyDown(Keys.Escape))
        {
            EndRebind();
            PlayCancel();
            return;
        }

        if (_rebindCapture.Poll(ks, GetPlayerGamePad(), _rebindingConfig.Keybind))
        {
            _rebindingConfig.Keybind.Save();
            EndRebind();
            PlayAccept();
        }
    }

    private void EndRebind()
    {
        _rebindingConfig = null;
        _rebindCapture = null;
    }

    private void ModifyValue(SaS2ModOptions.RegisteredConfig config, bool increase, bool fast = false)
    {
        var entry = GetActiveEntry(config);
        var type = entry.SettingType;

        if (type == typeof(bool))
        {
            ((ConfigEntry<bool>)entry).Value = !((ConfigEntry<bool>)entry).Value;
        }
        else if (type.IsEnum)
        {
            CycleEnum(entry, increase);
        }
        else if (type == typeof(int))
        {
            ((ConfigEntry<int>)entry).Value += increase ? 1 : -1;
        }
        else if (type == typeof(float))
        {
            if (fast) ((ConfigEntry<float>)entry).Value += increase ? 0.5f : -0.5f;
            else ((ConfigEntry<float>)entry).Value += increase ? 0.05f : -0.05f;
        }
        else if (type == typeof(string))
        {
            // Cycle through the registered acceptable values list (if provided).
            // Color strings (4-part comma format) are handled separately via the
            // color picker and never reach this branch.
            var acceptable = config.AcceptableValues;
            if (acceptable == null || acceptable.Length == 0) return;

            var current = (string)entry.BoxedValue ?? "";
            var idx = Array.IndexOf(acceptable, current);

            // If the stored value isn't in the list, snap to the first item
            if (idx < 0) idx = 0;
            else
                idx = increase
                    ? (idx + 1) % acceptable.Length
                    : (idx - 1 + acceptable.Length) % acceptable.Length;

            ((ConfigEntry<string>)entry).Value = acceptable[idx];
        }
    }

    private void ModifyColorComponent(SaS2ModOptions.RegisteredConfig config, int comp, bool inc)
    {
        var entry = GetActiveEntry(config);
        var parts = ((string)entry.BoxedValue).Split(',');
        if (parts.Length != 4) return;

        if (comp < 3) // RGB channels (0-255)
        {
            if (int.TryParse(parts[comp], out var v))
            {
                v = Clamp(inc ? v + 5 : v - 5, 0, 255);
                parts[comp] = v.ToString();
            }
        }
        else // Alpha channel (0.0-1.0)
        {
            if (float.TryParse(parts[comp], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = (float)Math.Round(Clamp(inc ? v + 0.05f : v - 0.05f, 0f, 1f), 2);
                parts[comp] = v.ToString(CultureInfo.InvariantCulture);
            }
        }

        entry.BoxedValue = string.Join(",", parts);
    }

    private static void CycleEnum(ConfigEntryBase entry, bool forward)
    {
        var values = Enum.GetValues(entry.SettingType);
        var index = Array.IndexOf(values, entry.BoxedValue);
        index = forward
            ? (index + 1) % values.Length
            : (index - 1 + values.Length) % values.Length;
        entry.BoxedValue = values.GetValue(index);
    }

    private static bool IsColorString(ConfigEntryBase entry) =>
        entry.SettingType == typeof(string) && ((string)entry.BoxedValue)?.Split(',').Length == 4;

    private static bool IsBool(ConfigEntryBase entry) => entry.SettingType == typeof(bool);

    /// Calculates tab rows that fit inside boxWidth, and returns total height used.
    /// Also draws the tabs if draw == true.
    private float LayoutAndDrawTabs(float boxX, float boxY, float boxWidth, bool draw)
    {
        if (!HasTabs) return 0f;

        var tabY = boxY + 6f;
        var tabH = TabBarHeight - 6f;

        // Pre-measure each tab width
        var tabWidths = new float[_tabs.Count];
        for (var t = 0; t < _tabs.Count; t++)
            tabWidths[t] = Text.GetStringSpace(new StringBuilder(_tabs[t]), 0.65f, player, 1) + TabPadX * 2f;

        // Build rows
        var rows = new List<List<int>>(); // list of tab indices per row
        var currentRow = new List<int>();
        var currentRowWidth = 0f;
        const float tabGap = 2f;

        for (var t = 0; t < _tabs.Count; t++)
        {
            var w = tabWidths[t];
            if (currentRow.Count > 0 && currentRowWidth + w + tabGap > boxWidth)
            {
                rows.Add(currentRow);
                currentRow = [];
                currentRowWidth = 0f;
            }

            currentRow.Add(t);
            currentRowWidth += w + (currentRow.Count > 1 ? tabGap : 0f);
        }

        if (currentRow.Count > 0) rows.Add(currentRow);

        var totalTabHeight = rows.Count * TabBarHeight;

        if (!draw) return totalTabHeight;

        // (Re)build the tab hit-test table for mouse clicks.
        if (_tabHitRects == null || _tabHitRects.Length != _tabs.Count)
            _tabHitRects = new Rectangle[_tabs.Count];

        // Draw each row, centered
        for (var r = 0; r < rows.Count; r++)
        {
            var rowIndices = rows[r];
            var rowTotalWidth = rowIndices.Sum(idx => tabWidths[idx]) + (rowIndices.Count - 1) * tabGap;
            var startX = boxX + (boxWidth - rowTotalWidth) / 2f;
            var curX = startX;
            var rowY = tabY + r * TabBarHeight;

            foreach (var idx in rowIndices)
            {
                var tw = tabWidths[idx];
                var rect = new Rectangle((int)curX, (int)rowY, (int)tw, (int)tabH);
                _tabHitRects[idx] = rect;

                if (idx == _currentTabIndex)
                {
                    UIRender.DrawRect(rect, 0.35f, 3, 1f, 1f, UIRender.interfaceTex);
                    Text.DrawText(new StringBuilder(_tabs[idx]),
                        new Vector2(curX + tw / 2f, rowY + tabH * 0.72f),
                        Color.Yellow, 0.65f, 1);
                }
                else
                {
                    UIRender.DrawRect(rect, 0.15f, 0, 1f, 1f, UIRender.interfaceTex);
                    Text.DrawText(new StringBuilder(_tabs[idx]),
                        new Vector2(curX + tw / 2f, rowY + tabH * 0.72f),
                        new Color(0.7f, 0.7f, 0.7f, 1f), 0.65f, 1);
                }

                curX += tw + tabGap;
            }
        }

        return totalTabHeight;
    }

    public override void Draw()
    {
        base.Draw();
        var vp = Game1.Instance.GraphicsDevice.Viewport;
        var boxWidth = vp.Width * 0.5f;
        var boxHeight = vp.Height * 0.8f;

        // Always assume local coop; menu takes place in respective player's side
        var margin = boxWidth * 0.025f;
        var isMainPlayer = player.ID == GameSessionMgr.gameSession.mainPlayerIdx;
        var boxX = isMainPlayer ? 0f - margin : vp.Width * 0.5f + margin;
        var boxY = (vp.Height - boxHeight) / 2f;

        UIRender.DrawRect(new Rectangle((int)boxX, (int)boxY, (int)boxWidth, (int)boxHeight), 0.85f, 0, 1f, 1f,
            UIRender.interfaceTex);

        // Draw tabs
        var usedTabHeight = LayoutAndDrawTabs(boxX, boxY, boxWidth, true);

        // Config list area
        _listX = boxX + 40f;
        _listY = boxY + TopMargin + usedTabHeight;
        _listWidth = boxWidth - 80f;
        var listVisibleHeight = boxHeight - TopMargin - usedTabHeight - BottomMargin;
        _currentListVisibleHeight = listVisibleHeight;

        var currentY = _listY - _scrollOffset;

        // Rebuild the item hit-test table each frame for mouse hover/click.
        _itemHitRects.Clear();

        string lastMod = null;
        for (var i = 0; i < _displayedConfigs.Count; i++)
        {
            var cfg = _displayedConfigs[i];
            var selected = i == _selectedIndex;

            // Section header (only when there are no tabs)
            if (!HasTabs && cfg.ModName != lastMod)
            {
                if (lastMod != null) currentY += 20f;
                if (currentY + SectionHeight > _listY && currentY < _listY + listVisibleHeight)
                {
                    Text.DrawText(new StringBuilder(cfg.ModName), new Vector2(_listX, currentY + 35f),
                        new Color(0.6f, 0.8f, 1f, 1f), 0.85f, 0);
                    UIRender.DrawDivider(new Vector2(_listX + _listWidth / 2f, currentY + 45f), 0.7f, 1f, 1f, 0.7f,
                        0.5f, 1, UIRender.interfaceTex);
                }

                currentY += SectionHeight;
                lastMod = cfg.ModName;
            }

            // Config row
            if (currentY + ItemHeight > _listY && currentY < _listY + listVisibleHeight)
            {
                _itemHitRects.Add(new ItemHit
                {
                    Index = i,
                    Rect = new Rectangle((int)_listX, (int)currentY, (int)_listWidth, (int)ItemHeight)
                });

                if (selected)
                    UIRender.DrawRect(new Rectangle((int)_listX, (int)currentY, (int)_listWidth, (int)ItemHeight), 0.2f,
                        3, 1f, 1f, UIRender.interfaceTex);

                var textColor = selected ? Color.Yellow : Color.White;
                var textY = currentY + ItemHeight * 0.75f;

                Text.DrawText(new StringBuilder(cfg.DisplayName), new Vector2(_listX + 10, textY), textColor, 0.7f, 0);

                var valStr = FormatValue(cfg, selected);
                Text.DrawText(new StringBuilder(valStr), new Vector2(_listX + _listWidth - ValueWidth, textY),
                    textColor, 0.7f, 0);
            }

            currentY += ItemHeight;
        }

        DrawHelpBar(boxX, boxWidth, vp.Height);
    }

    private void DrawHelpBar(float boxX, float boxWidth, float vpHeight)
    {
        var useKeyboard = player.inputProfile.keyMouseEnable;
        var action = useKeyboard ? "[Space]" : "[a]";
        var centerX = boxX + boxWidth / 2f;

        var selectedKeybind = _displayedConfigs is { Count: > 0 } && _selectedIndex >= 0 &&
                              _selectedIndex < _displayedConfigs.Count &&
                              _displayedConfigs[_selectedIndex].IsKeybind;

        // Line 1: edit / change / reset (/ enable toggle for keybinds).
        var top = new StringBuilder();
        top.Append($"\u02ef{action}\u02f0 Cycle/Edit  |  \u02ef[ll]/[lr]\u02f0 Change  |  ");
        top.Append(useKeyboard ? "\u02efBksp\u02f0 Reset" : "\u02ef[x]\u02f0 Reset");
        if (selectedKeybind)
            top.Append(useKeyboard ? "  |  \u02efTab\u02f0 On/Off" : "  |  \u02ef[y]\u02f0 On/Off");
        Text.DrawText(top, new Vector2(centerX, vpHeight - 62), Color.White, 0.6f, 1, player, 1);

        // Line 2: back / tab (kept on their own line so the bar does not get too wide).
        var bottom = new StringBuilder();
        bottom.Append("\u02ef[b]\u02f0 Back");
        if (HasTabs) bottom.Append(useKeyboard ? "  |  \u02ef[Z]/[X]\u02f0 Tab" : "  |  \u02ef[lt]/[rt]\u02f0 Tab");
        Text.DrawText(bottom, new Vector2(centerX, vpHeight - 38), Color.White, 0.6f, 1, player, 1);
    }

    private string FormatValue(SaS2ModOptions.RegisteredConfig config, bool selected)
    {
        var entry = GetActiveEntry(config);

        // Keybind: show the bound combo, or a prompt while capturing.
        if (config.IsKeybind)
            return _rebindingConfig == config ? "Press input... (Esc)" : config.Keybind.DisplayString();

        // Color string: show component highlight when actively editing
        if (IsColorString(entry) && selected && _colorCompIndex != -1)
        {
            var p = ((string)entry.BoxedValue).Split(',');
            p[_colorCompIndex] = ">" + p[_colorCompIndex] + "<";
            return string.Join(",", p);
        }

        // Bool
        if (entry.SettingType == typeof(bool)) return ((ConfigEntry<bool>)entry).Value ? "On" : "Off";

        // Float
        if (entry.SettingType == typeof(float)) return ((ConfigEntry<float>)entry).Value.ToString("F2");

        // String with acceptable-values list: show "Value (N/Total)"
        if (entry.SettingType == typeof(string) &&
            config.AcceptableValues is { Length: > 0 } values &&
            !IsColorString(entry))
        {
            var current = (string)entry.BoxedValue ?? "";
            var idx = Array.IndexOf((Array)values, current);
            var pos = idx >= 0 ? idx + 1 : 1; // snap display to 1 if value is unexpected
            return $"{current} ({pos}/{values.Length})";
        }

        return entry.BoxedValue?.ToString() ?? "null";
    }

    // Scrolling helpers
    private float GetItemY(int index)
    {
        float y = 0;
        string last = null;

        if (!HasTabs)
        {
            for (var i = 0; i <= index; i++)
            {
                if (_displayedConfigs[i].ModName != last)
                {
                    y += last == null ? SectionHeight : SectionHeight + 20f;
                    last = _displayedConfigs[i].ModName;
                }

                if (i < index) y += ItemHeight;
            }
        }
        else
        {
            y = index * ItemHeight;
        }

        return y;
    }

    private void EnsureVisible()
    {
        if (_displayedConfigs.Count == 0) return;

        var itemTop = GetItemY(_selectedIndex);
        var itemBottom = itemTop + ItemHeight;

        // Use the visible area that was computed during the last Draw()
        var visibleTop = _scrollOffset;
        var visibleBottom = _scrollOffset + _currentListVisibleHeight;

        if (itemTop < visibleTop)
            _scrollOffset = itemTop;
        else if (itemBottom > visibleBottom)
            _scrollOffset = itemBottom - _currentListVisibleHeight;

        _scrollOffset = Math.Max(0f, _scrollOffset);
    }

    private new void PlaySelect() => AccessTools.Method(typeof(LevelBase), "PlaySelect")?.Invoke(this, null);
    private new void PlayAccept() => AccessTools.Method(typeof(LevelBase), "PlayAccept")?.Invoke(this, null);
    private new void PlayCancel() => AccessTools.Method(typeof(LevelBase), "PlayCancel")?.Invoke(this, null);
    private new bool CanInput() => (bool)AccessTools.Method(typeof(LevelBase), "CanInput")?.Invoke(this, null)!;
}
