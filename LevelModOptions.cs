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

    // Tab state: row 1 = mods, row 2 = the selected mod's option categories
    private List<string> _modTabs;
    private List<string> _catTabs;
    private int _currentModIndex;
    private int _currentCatIndex;

    private enum TabFocus
    {
        Mods,
        Cats,
        List
    }

    private TabFocus _tabFocus = TabFocus.Mods;

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

    private Rectangle[] _modTabHitRects;
    private Rectangle[] _catTabHitRects;
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

        _modTabs = _allConfigs.Select(c => c.ModName).Distinct().ToList();
        _currentModIndex = 0;
        _currentCatIndex = 0;
        _tabFocus = TabFocus.Mods;

        RefreshDisplayedConfigs();
    }

    private void RefreshDisplayedConfigs()
    {
        if (_modTabs.Count == 0)
        {
            _displayedConfigs = [];
            return;
        }

        var mod = _modTabs[_currentModIndex];
        _catTabs = _allConfigs.Where(c => c.ModName == mod).Select(c => c.Category).Distinct().ToList();
        if (_currentCatIndex >= _catTabs.Count) _currentCatIndex = 0;

        if (_catTabs.Count == 0)
        {
            _displayedConfigs = [];
            return;
        }

        var cat = _catTabs[_currentCatIndex];
        _displayedConfigs = _allConfigs.Where(c => c.ModName == mod && c.Category == cat).ToList();
        _selectedIndex = 0;
        _scrollOffset = 0f;
        _colorCompIndex = -1;
        _fast = false;
    }

    private bool HasTabs => _modTabs.Count > 1 || _catTabs is { Count: > 1 };

    /// Switches to the next/previous mod and resets the category selection.
    private void SwitchMod(int dir)
    {
        _currentModIndex = (_currentModIndex + dir + _modTabs.Count) % _modTabs.Count;
        _currentCatIndex = 0;
        RefreshDisplayedConfigs();
        // The new mod may not have a category row; drop focus to the list in that case.
        if (_tabFocus == TabFocus.Cats && _catTabs.Count <= 1)
            _tabFocus = TabFocus.List;
    }

    /// Cycles the category selection, wrapping into the next/previous mod at the ends.
    private void CycleCategories(int dir)
    {
        if (_catTabs.Count > 1)
        {
            var next = _currentCatIndex + dir;
            if (next < 0 || next >= _catTabs.Count)
            {
                // At the first/last category: wrap into the previous/next mod.
                SwitchMod(dir);
                return;
            }
            _currentCatIndex = next;
            RefreshDisplayedConfigs();
            return;
        }

        // Single category: wrap into the next/previous mod.
        SwitchMod(dir);
    }

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

        // Tab navigation: Z/X switches tabs in the hovered row, or in the default row when nothing is hovered.
        // Categories wrap into the next/previous mod at the ends.
        if (player.keys.keyCatLeft || player.keys.keyCatRight)
        {
            var dir = player.keys.keyCatRight ? 1 : -1;
            var mouse = MouseMgr.mLoc;

            var hoveringMods = _modTabHitRects != null && _modTabHitRects.Any(r => PointInRect(mouse, r));
            var hoveringCats = _catTabs.Count > 1 && _catTabHitRects != null &&
                               _catTabHitRects.Any(r => PointInRect(mouse, r));

            if (hoveringMods || (!hoveringCats && _tabFocus == TabFocus.Mods))
            {
                SwitchMod(dir);
                PlaySelect();
                return;
            }

            if (hoveringCats || _tabFocus == TabFocus.Cats)
            {
                CycleCategories(dir);
                PlaySelect();
                return;
            }

            // Nothing hovered and focus is on the list: cycle categories when there are several, otherwise cycle mods.

            if (_catTabs.Count > 1)
                CycleCategories(dir);
            else
                SwitchMod(dir);
            PlaySelect();
            return;
        }

        if (player.keys.keyUp || player.keys.keyDown)
        {
            var up = player.keys.keyUp;
            switch (_tabFocus)
            {
                case TabFocus.Mods:
                    // Down enters the category row, or the list when there is only one category.
                    _tabFocus = _catTabs.Count > 1 ? TabFocus.Cats : TabFocus.List;
                    PlaySelect();
                    return;

                case TabFocus.Cats:
                    if (up)
                    {
                        _tabFocus = TabFocus.Mods;
                        PlaySelect();
                        return;
                    }
                    _tabFocus = TabFocus.List;
                    PlaySelect();
                    return;

                case TabFocus.List:
                    if (up && _selectedIndex == 0)
                    {
                        _tabFocus = _catTabs.Count > 1 ? TabFocus.Cats : TabFocus.Mods;
                        PlaySelect();
                        return;
                    }
                    if (_displayedConfigs.Count == 0) return;
                    break;
            }

            var dir = up ? -1 : 1;
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

        // Mod row clicks switch mods.
        if (leftClick && _modTabHitRects != null)
        {
            for (var t = 0; t < _modTabHitRects.Length && t < _modTabs.Count; t++)
            {
                if (!PointInRect(mouse, _modTabHitRects[t])) continue;
                player.menu.mouseInRect = true;
                if (t != _currentModIndex)
                {
                    _currentModIndex = t;
                    _currentCatIndex = 0;
                    RefreshDisplayedConfigs();
                    PlaySelect();
                }
                _tabFocus = TabFocus.Mods;
                return true;
            }
        }

        // Category row clicks switch categories.
        if (leftClick && _catTabHitRects != null)
        {
            for (var t = 0; t < _catTabHitRects.Length && t < _catTabs.Count; t++)
            {
                if (!PointInRect(mouse, _catTabHitRects[t])) continue;
                player.menu.mouseInRect = true;
                if (t != _currentCatIndex)
                {
                    _currentCatIndex = t;
                    RefreshDisplayedConfigs();
                    PlaySelect();
                }
                _tabFocus = TabFocus.Cats;
                return true;
            }
        }

        // Hovering a category tab focuses the category row so Z/X switches categories.
        if (moved && _catTabHitRects != null)
        {
            for (var t = 0; t < _catTabHitRects.Length && t < _catTabs.Count; t++)
            {
                if (!PointInRect(mouse, _catTabHitRects[t])) continue;
                player.menu.mouseInRect = true;
                _tabFocus = TabFocus.Cats;
                return false;
            }
        }

        // Hovering a mod tab focuses the mod row.
        if (moved && _modTabHitRects != null)
        {
            for (var t = 0; t < _modTabHitRects.Length && t < _modTabs.Count; t++)
            {
                if (!PointInRect(mouse, _modTabHitRects[t])) continue;
                player.menu.mouseInRect = true;
                _tabFocus = TabFocus.Mods;
                return false;
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

    /// Draws the two tab rows (mods on top, the selected mod's categories below) and returns the total height used.
    /// Each row wraps onto multiple lines when needed.
    /// The category row is skipped entirely when the selected mod has only one category.
    private float LayoutAndDrawTabs(float boxX, float boxY, float boxWidth, bool draw)
    {
        if (!HasTabs) return 0f;

        var tabY = boxY + 6f;
        var tabH = TabBarHeight - 6f;

        // Pre-measure each tab width
        var modWidths = new float[_modTabs.Count];
        for (var t = 0; t < _modTabs.Count; t++)
            modWidths[t] = Text.GetStringSpace(new StringBuilder(_modTabs[t]), 0.65f, player, 1) + TabPadX * 2f;

        var showCatRow = _catTabs.Count > 1;
        var catWidths = new float[showCatRow ? _catTabs.Count : 0];
        for (var t = 0; t < catWidths.Length; t++)
            catWidths[t] = Text.GetStringSpace(new StringBuilder(_catTabs[t]), 0.65f, player, 1) + TabPadX * 2f;

        // Build rows
        var modRows = BuildTabRows(modWidths, boxWidth);
        var catRows = showCatRow ? BuildTabRows(catWidths, boxWidth) : [];

        var totalTabHeight = (modRows.Count + catRows.Count) * TabBarHeight
            + (catRows.Count > 0 ? TabDividerHeight : 0f);

        if (!draw) return totalTabHeight;

        // (Re)build the tab hit-test tables for mouse clicks.
        if (_modTabHitRects == null || _modTabHitRects.Length != _modTabs.Count)
            _modTabHitRects = new Rectangle[_modTabs.Count];
        if (_catTabHitRects == null || _catTabHitRects.Length != _catTabs.Count)
            _catTabHitRects = new Rectangle[_catTabs.Count];

        // Mod row(s), centered
        var rowY = tabY;
        foreach (var rowIndices in modRows)
        {
            var rowTotalWidth = rowIndices.Sum(idx => modWidths[idx]) + (rowIndices.Count - 1) * TabGap;
            var curX = boxX + (boxWidth - rowTotalWidth) / 2f;

            foreach (var idx in rowIndices)
            {
                var tw = modWidths[idx];
                var rect = new Rectangle((int)curX, (int)rowY, (int)tw, (int)tabH);
                _modTabHitRects[idx] = rect;

                var selected = idx == _currentModIndex;
                var focused = selected && _tabFocus == TabFocus.Mods;
                DrawTab(rect, _modTabs[idx], focused, selected);

                curX += tw + TabGap;
            }

            rowY += TabBarHeight;
        }

        // Divider between the mod row and the category row.
        if (catRows.Count > 0)
        {
            rowY += TabDividerHeight * 0.5f;
            UIRender.DrawDivider(new Vector2(boxX + boxWidth / 2f, rowY), 0.7f, 1f, 1f, 1f, 0.5f, 1,
                UIRender.interfaceTex);
            rowY += TabDividerHeight * 0.5f;
        }

        // Category row(s), centered
        foreach (var rowIndices in catRows)
        {
            var rowTotalWidth = rowIndices.Sum(idx => catWidths[idx]) + (rowIndices.Count - 1) * TabGap;
            var curX = boxX + (boxWidth - rowTotalWidth) / 2f;

            foreach (var idx in rowIndices)
            {
                var tw = catWidths[idx];
                var rect = new Rectangle((int)curX, (int)rowY, (int)tw, (int)tabH);
                _catTabHitRects[idx] = rect;

                var selected = idx == _currentCatIndex;
                var focused = selected && _tabFocus == TabFocus.Cats;
                DrawTab(rect, _catTabs[idx], focused, selected);

                curX += tw + TabGap;
            }

            rowY += TabBarHeight;
        }

        return totalTabHeight;
    }

    private const float TabGap = 2f;
    private const float TabDividerHeight = 10f;

    /// Builds the list of rows that fit inside boxWidth, each row a list of tab indices.
    private static List<List<int>> BuildTabRows(float[] tabWidths, float boxWidth)    {
        var rows = new List<List<int>>();
        var currentRow = new List<int>();
        var currentRowWidth = 0f;

        for (var t = 0; t < tabWidths.Length; t++)
        {
            var w = tabWidths[t];
            if (currentRow.Count > 0 && currentRowWidth + w + TabGap > boxWidth)
            {
                rows.Add(currentRow);
                currentRow = [];
                currentRowWidth = 0f;
            }

            currentRow.Add(t);
            currentRowWidth += w + (currentRow.Count > 1 ? TabGap : 0f);
        }

        if (currentRow.Count > 0) rows.Add(currentRow);
        return rows;
    }

    /// Draws a single tab. Focused tabs get a bright border, selected tabs are yellow.
    private void DrawTab(Rectangle rect, string label, bool focused, bool selected)    {
        if (focused)
        {
            UIRender.DrawRect(rect, 0.35f, 3, 1f, 1f, UIRender.interfaceTex);
            Text.DrawText(new StringBuilder(label),
                new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height * 0.72f),
                Color.Yellow, 0.65f, 1);
        }
        else if (selected)
        {
            UIRender.DrawRect(rect, 0.25f, 0, 1f, 1f, UIRender.interfaceTex);
            Text.DrawText(new StringBuilder(label),
                new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height * 0.72f),
                Color.Yellow, 0.65f, 1);
        }
        else
        {
            UIRender.DrawRect(rect, 0.15f, 0, 1f, 1f, UIRender.interfaceTex);
            Text.DrawText(new StringBuilder(label),
                new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height * 0.72f),
                new Color(0.7f, 0.7f, 0.7f, 1f), 0.65f, 1);
        }
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

        for (var i = 0; i < _displayedConfigs.Count; i++)
        {
            var cfg = _displayedConfigs[i];
            var selected = i == _selectedIndex;

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

        // Fast stepping only applies to float options (Accept toggles it).
        var selectedFloat = _displayedConfigs is { Count: > 0 } && _selectedIndex >= 0 &&
                            _selectedIndex < _displayedConfigs.Count &&
                            GetActiveEntry(_displayedConfigs[_selectedIndex]).SettingType == typeof(float);

        // Line 1: edit / change / reset (/ enable toggle for keybinds, / fast stepping for floats).
        var top = new StringBuilder();
        top.Append($"\u02ef{action}\u02f0 Cycle/Edit  |  \u02ef[ll]/[lr]\u02f0 Change  |  ");
        top.Append(useKeyboard ? "\u02efBksp\u02f0 Reset" : "\u02ef[x]\u02f0 Reset");
        if (selectedKeybind)
            top.Append(useKeyboard ? "  |  \u02efTab\u02f0 On/Off" : "  |  \u02ef[y]\u02f0 On/Off");
        else if (selectedFloat)
            top.Append($"  |  \u02ef{action}\u02f0 Fast: {(_fast ? "On" : "Off")}");
        Text.DrawText(top, new Vector2(centerX, vpHeight - 62), Color.White, 0.6f, 1, player, 1);

        // Line 2: back / tab (kept on their own line so the bar does not get too wide).
        var bottom = new StringBuilder();
        bottom.Append("\u02ef[b]\u02f0 Back");
        if (HasTabs)
            bottom.Append(useKeyboard ? "  |  \u02ef[Z]/[X]\u02f0 Tab  |  \u02ef[Up]/[Dn]\u02f0 Row" : "  |  \u02ef[lt]/[rt]\u02f0 Tab  |  \u02ef[up]/[dn]\u02f0 Row");
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
        if (entry.SettingType == typeof(float))
        {
            var value = ((ConfigEntry<float>)entry).Value;
            if (config.IsPercent) return $"{Math.Round(value * 100f)}%";
            return value.ToString("F2");
        }

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
    private float GetItemY(int index) => index * ItemHeight;

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
