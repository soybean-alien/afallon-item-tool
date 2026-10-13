using AfallonItemSpawner.Core;
using BLINK.RPGBuilder.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// Injected host component. It only forwards Unity callbacks; all UI logic lives in the
/// plain managed <see cref="SpawnerWindow"/> so nothing unusual crosses the IL2CPP boundary.
/// IMGUI is deliberately not used: on this Unity 6.3 / Il2CppInterop build, GUILayout,
/// TextField, BeginScrollView and DrawTexture all failed. uGUI is what the game itself uses.
/// </summary>
internal sealed class ItemSpawnerController : MonoBehaviour
{
    private SpawnerWindow _window;
    private bool _faulted;

    public ItemSpawnerController(IntPtr pointer) : base(pointer)
    {
    }

    private void Start()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _window = new SpawnerWindow();
            _window.Build();
            CatalogProbeState.Log?.LogInfo($"Item-spawner UI build took {watch.ElapsedMilliseconds} ms.");
        }
        catch (Exception exception)
        {
            _faulted = true;
            CatalogProbeState.Log?.LogError($"Item-spawner UI build failed: {exception}");
        }
    }

    private void Update()
    {
        if (_faulted || _window is null) return;
        try
        {
            _window.Tick();
        }
        catch (Exception exception)
        {
            _faulted = true;
            CatalogProbeState.Log?.LogError($"Item-spawner UI disabled after error: {exception}");
            _window.Hide();
        }
    }
}

internal sealed class SpawnerWindow
{
    // Layout is authored in "1080p units"; the canvas scale factor maps them to real pixels.
    private const float Width = 640f;
    private const float Height = 652f;
    private const float RowHeight = 44f;
    private const float Margin = 12f;
    private const float ListTop = 134f;
    private const float FooterHeight = 180f;
    private const float PreviewWidth = 360f;
    private const float PreviewGap = -12f; // docked: overlap the two panels' 6px transparent glow margins so their gold edges merge into one seam
    private const float ThumbHeight = 44f;
    private const string HiddenCategory = "Hidden";

    private static readonly Color PanelColor = new(0.10f, 0.12f, 0.15f, 0.97f);
    private static readonly Color HeaderColor = new(0.16f, 0.19f, 0.24f, 1f);
    private static readonly Color FieldColor = new(0.06f, 0.07f, 0.09f, 1f);
    private static readonly Color ButtonColor = new(0.22f, 0.25f, 0.30f, 1f);
    private static readonly Color AccentColor = new(0.22f, 0.58f, 0.30f, 1f);
    private static readonly Color WarnColor = new(0.62f, 0.42f, 0.12f, 1f);
    private static readonly Color MutedText = new(0.70f, 0.72f, 0.75f, 1f);
    private static readonly Color TagColor = new(0.95f, 0.72f, 0.30f, 1f);
    private static readonly Color Unranked = new(0.28f, 0.31f, 0.34f, 1f);

    private static readonly (string Label, string Category, float Width)[] Categories =
    {
        ("All", "All", 46f), ("Weapons", "Weapon", 76f), ("Armor", "Armor", 60f),
        ("Equipment", "Equipment", 88f), ("Consumables", "Consumable", 104f),
        ("Crafting", "CraftingSupply", 74f), ("Reagents", "Reagent", 76f),
        ("Quest", "Quest", 56f), ("Currency", "Currency", 76f), ("Misc", "Miscellaneous", 50f),
        ("Hidden / Dev", HiddenCategory, 104f)
    };

    private readonly Dictionary<string, Image> _categoryButtons = new();
    private readonly List<int> _visible = new();
    private SpawnCoordinator _coordinator;
    private GameItemGrantService _grantService;
    private string _bullet = "- ";
    private int _corruption;
    private TextMeshProUGUI _corruptionValue;
    private TextMeshProUGUI _corruptionLabel;
    private Button _corruptionMinus;
    private Button _corruptionPlus;
    private Button _corruptionMax;
    private Image _previewFrame;
    private Image _previewIcon;
    private TextMeshProUGUI _previewName;
    private TextMeshProUGUI _previewBody;
    private RectTransform _previewContent;
    private ScrollRect _previewScroll;
    private GameObject _root;
    private CanvasScaler _scaler;
    private RectTransform _panel;
    private RectTransform _header;
    private RectTransform _content;
    private RectTransform _viewport;
    private ScrollRect _scroll;
    private Slider _thumb;
    private TMP_FontAsset _font;
    private bool _bundledFont;
    private string _sep = " | ";
    private TMP_InputField _search;
    private TMP_InputField _quantity;
    private TextMeshProUGUI _countLabel;
    private TextMeshProUGUI _sortLabel;
    private TextMeshProUGUI _classLabel;
    private Image _classButton;
    private int _classFilter = -1; // index into ItemClassIndex.Classes; -1 = any class
    private int _sortMode;
    private readonly List<(string Label, int Kind, int[] StatIds)> _sortOptions = new();
    private const int SortBest = 0, SortLevel = 1, SortName = 2, SortId = 3, SortStat = 4;
    private GameObject _sortDropdown;
    private GameObject _classDropdown;
    private float _dropdownY;
    private float _sortDropdownX;
    private float _classDropdownX;
    private TextMeshProUGUI _selectedName;
    private TextMeshProUGUI _selectedInfo;
    private TextMeshProUGUI _status;
    private TextMeshProUGUI _questLabel;
    private Image _questButton;
    private Image _selectedIcon;
    private Image _selectedFrame;
    private Button _addButton;
    private TextMeshProUGUI _addLabel;
    private GameObject[] _rows;
    private Image[] _rowBackgrounds;
    private RectTransform[] _rowRects;
    private Image[] _rowIcons;
    private bool[] _iconReady;
    private float _nextIconRetry;
    private int _lastReadyCount = -1;
    private int _iconPassesWithoutChange;
    private string _category = "All";
    private string _query = string.Empty;
    private int _selected = -1;
    private bool _visibleWindow = true;
    private bool _dragging;
    private Vector3 _lastMouse;
    private bool _typingSignalled;
    private bool _syncingThumb;
    private float _nextStateRefresh;
    private float _nextEventSystemCheck;
    private bool _warnedNoEventSystem;
    private float _scale = 1f;
    private int _lastScreenHeight = -1;
    private int _lastScreenWidth = -1;

    public void Build()
    {
        RebuildPolicy();
        _font = LoadBundledFont();
        _bundledFont = _font is not null;
        _font ??= ChooseFont();
        LoadSkin();
        _sep = FontHas('·') ? "  ·  " : "  |  ";
        _bullet = FontHas('•') ? "• " : "- ";

        _root = new GameObject("soybean_alien_ItemSpawnerCanvas");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        _scaler = _root.AddComponent<CanvasScaler>();
        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        _root.AddComponent<GraphicRaycaster>();
        UpdateScale(force: true);

        _panel = NewRect(_root.transform, "Window");
        _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
        _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.sizeDelta = new Vector2(Width, Height);
        // Centre the list window plus the preview panel to its right as one block, unless the user
        // dragged it somewhere last session.
        _panel.anchoredPosition = LoadWindowPosition() ?? new Vector2(-(PreviewWidth + PreviewGap) / 2f, 0f);
        ClampPanelToScreen();
        SkinWindow(_panel, AddImage(_panel.gameObject, PanelColor, true));

        BuildHeader();
        BuildSearch();
        BuildCategories();
        BuildList();
        BuildFooter();
        BuildPreview();
        BuildDropdowns();
        ApplyFilter();
        RefreshSelection();
        RefreshQuestToggle();
        CatalogProbeState.Log?.LogInfo($"Item-spawner window built: {_rows.Length} rows, font={(_font is null ? "TMP default" : _font.name)}, scale={_scale:0.00}.");
    }

    public void Tick()
    {
        var toggle = Plugin.ToggleKey?.Value ?? KeyCode.F8;
        if (Input.GetKeyDown(toggle))
        {
            if (_visibleWindow) Hide(); else Show();
        }

        if (!_visibleWindow) return;

        if (Time.unscaledTime >= _nextEventSystemCheck)
        {
            _nextEventSystemCheck = Time.unscaledTime + 2f;
            UpdateScale(force: false);
            if (!_warnedNoEventSystem && EventSystem.current is null)
            {
                _warnedNoEventSystem = true;
                CatalogProbeState.Log?.LogWarning("No EventSystem in the scene; spawner buttons cannot receive clicks here.");
            }
        }

        HandleDrag();
        UpdateTypingSignal();

        if (Time.unscaledTime >= _nextStateRefresh)
        {
            _nextStateRefresh = Time.unscaledTime + 0.25f;
            RefreshAddButton();
        }

        if (_iconPassesWithoutChange < 5 && Time.unscaledTime >= _nextIconRetry)
        {
            _nextIconRetry = Time.unscaledTime + 3f;
            ResolveIcons();
        }
    }

    public void Show()
    {
        _visibleWindow = true;
        _root.SetActive(true);
    }

    public void Hide()
    {
        _visibleWindow = false;
        if (_typingSignalled)
        {
            _typingSignalled = false;
            UIEvents.Instance?.OnPlayerStopTyping();
        }

        if (_root is not null) _root.SetActive(false);
    }

    // ---------- scale and font ----------

    private void UpdateScale(bool force)
    {
        if (!force && Screen.height == _lastScreenHeight && Screen.width == _lastScreenWidth) return;
        _lastScreenHeight = Screen.height;
        _lastScreenWidth = Screen.width;
        var user = Math.Clamp(Plugin.UiScale?.Value ?? 1f, 0.5f, 3f);
        var auto = Math.Clamp(Screen.height / 1080f, 0.5f, 4f);
        // Never let the window outgrow the screen.
        var fit = Math.Min(Screen.width / (Width + PreviewGap + PreviewWidth + 40f), Screen.height / (Height + 40f));
        _scale = Math.Max(0.4f, Math.Min(auto * user, fit));
        _scaler.scaleFactor = _scale;
        if (_panel is not null) ClampPanelToScreen();
        if (!force) CatalogProbeState.Log?.LogInfo($"Screen {Screen.width}x{Screen.height}: menu scale {_scale:0.00}.");
    }

    private static TMP_FontAsset ChooseFont()
    {
        try
        {
            var wanted = Plugin.FontName?.Value?.Trim();
            var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            var byName = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
                if (asset is not null && !string.IsNullOrEmpty(asset.name)) byName[asset.name] = asset;

            if (!string.IsNullOrEmpty(wanted))
            {
                if (byName.TryGetValue(wanted, out var configured)) return configured;
                CatalogProbeState.Log?.LogWarning($"Configured FontName '{wanted}' not found; choosing automatically.");
            }

            // Count which fonts the game's own UI text actually uses.
            var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                var name = text?.font?.name;
                if (string.IsNullOrEmpty(name)) continue;
                usage[name] = usage.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            CatalogProbeState.Log?.LogInfo("TMP fonts in use by game UI: "
                + string.Join(", ", usage.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key}={p.Value}"))
                + $". Loaded font assets: {string.Join(", ", byName.Keys.Take(20))}.");

            foreach (var candidate in usage.OrderByDescending(p => p.Value).Select(p => p.Key))
            {
                if (candidate.StartsWith("LiberationSans", StringComparison.OrdinalIgnoreCase)) continue;
                if (byName.TryGetValue(candidate, out var asset) && HasBasicLatin(asset)) return asset;
            }
        }
        catch (Exception exception)
        {
            CatalogProbeState.Log?.LogWarning($"Font discovery failed, using TMP default: {exception.Message}");
        }

        return TMP_Settings.defaultFontAsset;
    }

    /// <summary>
    /// Loads the bundled Inter font (fonts\Inter-Regular.ttf next to the plugin DLL, SIL OFL 1.1) as a
    /// dynamic TextMeshPro font. Returns null - so the game font is used - when the user set
    /// Display.FontName to a game font, or the file is missing, or loading fails.
    /// </summary>
    private static TMP_FontAsset LoadBundledFont()
    {
        if (!string.Equals(Plugin.FontName?.Value?.Trim(), "Geist", StringComparison.OrdinalIgnoreCase)) return null; // opt-in
        try
        {
            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
            var path = Path.Combine(dir, "fonts", "Geist-Medium.ttf");
            if (!File.Exists(path))
            {
                CatalogProbeState.Log?.LogWarning($"Bundled font not found at {path}; using the game font.");
                return null;
            }

            var font = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, true);
            if (font is null)
            {
                CatalogProbeState.Log?.LogWarning("Geist could not be loaded; using the game font.");
                return null;
            }

            font.name = "Geist-Medium (Item Tool)";
            font.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CatalogProbeState.Log?.LogInfo($"Font: bundled Geist loaded from {path}.");
            return font;
        }
        catch (Exception exception)
        {
            CatalogProbeState.Log?.LogWarning($"Geist load failed ({exception.GetType().Name}: {exception.Message}); using the game font.");
            return null;
        }
    }

    /// <summary>Most-used loaded TMP font whose name starts with the given prefix (null if none).</summary>
    private static TMP_FontAsset FindFont(string prefix)
    {
        try
        {
            var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                var name = text?.font?.name;
                if (!string.IsNullOrEmpty(name)) usage[name] = usage.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            TMP_FontAsset best = null;
            var bestUse = -1;
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (asset is null || string.IsNullOrEmpty(asset.name) || !asset.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !HasBasicLatin(asset)) continue;
                var use = usage.TryGetValue(asset.name, out var u) ? u : 0;
                if (use > bestUse) { best = asset; bestUse = use; }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasBasicLatin(TMP_FontAsset font)
    {
        try
        {
            return font.HasCharacters("Aa09ZzIDx+-");
        }
        catch
        {
            return true;
        }
    }

    private bool FontHas(char c)
    {
        try
        {
            return _font is null || _font.HasCharacter(c, true, false);
        }
        catch
        {
            return false;
        }
    }

    // ---------- layout ----------

    private void BuildHeader()
    {
        _header = Place(NewRect(_panel, "Header"), 0f, 0f, Width, 32f);
        SkinHeader(AddImage(_header.gameObject, HeaderColor, true));
        var title = AddText(_header, "Title", "Afallon Item Tool", 16f, _skinned ? new Color(1f, 0.86f, 0.52f, 1f) : Color.white);
        Place(title.rectTransform, Margin, 0f, Width - 160f, 32f);
        // Title uses the same regular style as the item list (user request).
        var hint = AddText(_header, "Hint", $"{Plugin.ToggleKey?.Value ?? KeyCode.F8} hides  |  drag to move", 16f, _skinned ? new Color(1f, 0.86f, 0.52f, 1f) : MutedText);
        hint.alignment = TextAlignmentOptions.MidlineRight;
        Place(hint.rectTransform, Width - 290f, 0f, 226f, 32f);
        var close = AddButton(_header, "Close", "X", new Color(0.55f, 0.18f, 0.18f, 1f), Hide);
        Place(Rt(close), Width - 50f, 5f, 30f, 22f); // clear of the docked preview's overlapping edge
    }

    private void BuildSearch()
    {
        _search = AddInputField(_panel, "Search", "Search name, ID, type, rarity or source...", TMP_InputField.ContentType.Standard);
        Place(Rt(_search), Margin, 40f, Width - 2f * Margin, 30f);
        _search.onValueChanged.AddListener(new Action<string>(value =>
        {
            _query = value ?? string.Empty;
            ApplyFilter();
        }));
    }

    private void BuildCategories()
    {
        var x = Margin;
        var y = 76f;
        foreach (var (label, category, width) in Categories)
        {
            if (x + width > Width - Margin)
            {
                x = Margin;
                y += 28f;
            }

            var captured = category;
            var button = AddButton(_panel, "Cat_" + category, label, ButtonColor, () =>
            {
                _category = captured;
                ApplyFilter();
            });
            Place(Rt(button), x, y, width, 24f);
            _categoryButtons[category] = button.GetComponent<Image>();
            x += width + 4f;
        }

        BuildSortOptions();
        var sortX = x + 8f;
        var sort = AddButton(_panel, "Sort", string.Empty, ButtonColor, () => ToggleDropdown(_sortDropdown));
        Place(Rt(sort), sortX, y, 112f, 24f);
        _sortLabel = sort.GetComponentInChildren<TextMeshProUGUI>();
        _sortLabel.fontSize = 12f;
        _sortLabel.text = SortLabel();

        var classX = x + 124f;
        var cls = AddButton(_panel, "ClassFilter", string.Empty, ButtonColor, () => ToggleDropdown(_classDropdown));
        Place(Rt(cls), classX, y, 128f, 24f);
        _classButton = cls.GetComponent<Image>();
        _classLabel = cls.GetComponentInChildren<TextMeshProUGUI>();
        _classLabel.fontSize = 12f;
        _classLabel.text = ClassLabel();

        _dropdownY = y + 26f;
        _sortDropdownX = sortX;
        _classDropdownX = classX;

        _countLabel = AddText(_panel, "Count", string.Empty, 12f, MutedText);
        _countLabel.alignment = TextAlignmentOptions.MidlineRight;
        var left = x + 256f;
        Place(_countLabel.rectTransform, left, y, Width - Margin - left, 24f);
    }

    private void BuildList()
    {
        var area = Place(NewRect(_panel, "List"), Margin, ListTop, Width - 2f * Margin, Height - ListTop - FooterHeight);
        AddImage(area.gameObject, FieldColor, true);
        _scroll = area.gameObject.AddComponent<ScrollRect>();

        _viewport = NewRect(area, "Viewport");
        Stretch(_viewport, 2f, 2f, 20f, 2f);
        _viewport.gameObject.AddComponent<RectMask2D>();
        AddImage(_viewport.gameObject, new Color(0f, 0f, 0f, 0.01f), true);

        _content = NewRect(_viewport, "Content");
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        // A Slider with a fixed-height handle instead of a Scrollbar: a Scrollbar's handle shrinks to a
        // sliver over 1,000+ rows, which made the list look short. The thumb stays grabbable.
        var bar = NewRect(area, "ScrollThumb");
        bar.anchorMin = new Vector2(1f, 0f);
        bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(1f, 0.5f);
        bar.sizeDelta = new Vector2(16f, -4f);
        bar.anchoredPosition = new Vector2(-2f, 0f);
        AddImage(bar.gameObject, new Color(0.14f, 0.16f, 0.19f, 1f), true);
        var slideArea = NewRect(bar, "Handle Slide Area");
        Stretch(slideArea, 0f, ThumbHeight / 2f, 0f, ThumbHeight / 2f);
        var handle = NewRect(slideArea, "Handle");
        handle.sizeDelta = new Vector2(0f, ThumbHeight);
        var handleImage = AddImage(handle.gameObject, new Color(0.52f, 0.57f, 0.64f, 1f), true);
        if (_skinned)
        {
            handleImage.sprite = _buttonBody;
            handleImage.type = Image.Type.Sliced;
            handleImage.color = IdleBody; // gold
        }
        _thumb = bar.gameObject.AddComponent<Slider>();
        _thumb.handleRect = handle;
        _thumb.targetGraphic = handleImage;
        _thumb.direction = Slider.Direction.BottomToTop;
        _thumb.minValue = 0f;
        _thumb.maxValue = 1f;
        _thumb.SetValueWithoutNotify(1f);
        _thumb.onValueChanged.AddListener(new Action<float>(value =>
        {
            if (_syncingThumb) return;
            _syncingThumb = true;
            _scroll.verticalNormalizedPosition = value;
            _syncingThumb = false;
            UpdatePositionLabel();
        }));

        _scroll.content = _content;
        _scroll.viewport = _viewport;
        _scroll.horizontal = false;
        _scroll.vertical = true;
        _scroll.movementType = ScrollRect.MovementType.Clamped;
        _scroll.inertia = false;
        _scroll.scrollSensitivity = RowHeight * 3f;
        _scroll.onValueChanged.AddListener(new Action<Vector2>(_ =>
        {
            if (!_syncingThumb)
            {
                _syncingThumb = true;
                _thumb.SetValueWithoutNotify(_scroll.verticalNormalizedPosition);
                _syncingThumb = false;
            }

            UpdatePositionLabel();
        }));

        BuildRows();
    }

    private void BuildRows()
    {
        var ids = CatalogProbeState.ItemIds;
        var names = CatalogProbeState.ItemNames;
        var rarities = CatalogProbeState.ItemRarities;
        var types = CatalogProbeState.ItemTypes;
        var colors = CatalogProbeState.ItemRarityColors;
        var items = GameDatabase.Instance.GetItems();
        var textWidth = Width - 2f * Margin - 22f - 56f;
        _rows = new GameObject[ids.Length];
        _rowBackgrounds = new Image[ids.Length];
        _rowRects = new RectTransform[ids.Length];
        _rowIcons = new Image[ids.Length];
        _iconReady = new bool[ids.Length];
        var iconFailures = 0;

        for (var i = 0; i < ids.Length; i++)
        {
            var index = i;
            var color = colors?[i] ?? Unranked;
            var row = NewRect(_content, "Row_" + ids[i]);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(-4f, RowHeight - 2f);
            var background = AddImage(row.gameObject, RowTint(color, false), true);
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(new Action(() => Select(index)));

            var frame = Place(NewRect(row, "Frame"), 4f, 3f, 36f, 36f);
            var frameImage = AddImage(frame.gameObject, Darken(color), false);
            var icon = NewRect(frame, "Icon");
            Stretch(icon, 2f, 2f, 2f, 2f);
            var iconImage = AddImage(icon.gameObject, Color.white, false);
            iconImage.preserveAspect = true;
            try
            {
                var item = items[ids[i]];
                var sprite = IconOf(item);
                if (sprite is not null)
                {
                    iconImage.sprite = sprite;
                    _iconReady[i] = true;
                }
                else
                {
                    iconImage.color = Transparent;
                }

                var rarityBackground = item?.ItemRarity?.background;
                if (rarityBackground is not null)
                {
                    frameImage.sprite = rarityBackground;
                    frameImage.color = Color.white;
                }
            }
            catch (Exception exception)
            {
                iconImage.color = Transparent;
                if (iconFailures++ == 0)
                    CatalogProbeState.Log?.LogWarning($"Icon lookup failed for item {ids[i]}: {exception.Message}");
            }

            var tag = TagFor(i);
            var name = AddText(row, "Name", names[i], 15f, Brighten(color));
            Place(name.rectTransform, 48f, 3f, textWidth - (tag.Length > 0 ? 96f : 0f), 20f);
            if (tag.Length > 0)
            {
                var tagText = AddText(row, "Tag", tag, 11f, TagColor);
                tagText.alignment = TextAlignmentOptions.MidlineRight;
                Place(tagText.rectTransform, 48f + textWidth - 92f, 3f, 92f, 20f);
            }

            var rarity = string.IsNullOrWhiteSpace(rarities?[i]) ? "Unranked" : rarities[i];
            var level = CatalogProbeState.ItemLevels?[i] ?? 0;
            var info = AddText(row, "Info", $"{(level > 0 ? $"Lvl {level}{_sep}" : string.Empty)}{types[i]}{_sep}{rarity}", 12f, MutedText);
            Place(info.rectTransform, 48f, 22f, textWidth, 17f);

            _rows[i] = row.gameObject;
            _rowRects[i] = row;
            _rowBackgrounds[i] = background;
            _rowIcons[i] = iconImage;
        }

        if (iconFailures > 0)
            CatalogProbeState.Log?.LogWarning($"{iconFailures} item icons could not be resolved.");
    }

    private void BuildFooter()
    {
        var y = Height - FooterHeight + 8f;
        var frame = Place(NewRect(_panel, "SelectedFrame"), Margin, y, 44f, 44f);
        _selectedFrame = AddImage(frame.gameObject, FieldColor, false);
        var icon = NewRect(frame, "SelectedIcon");
        Stretch(icon, 3f, 3f, 3f, 3f);
        _selectedIcon = AddImage(icon.gameObject, Color.white, false);
        _selectedIcon.preserveAspect = true;

        _selectedName = AddText(_panel, "SelectedName", string.Empty, 15f, Color.white);
        Place(_selectedName.rectTransform, 64f, y, 300f, 22f);
        _selectedInfo = AddText(_panel, "SelectedInfo", string.Empty, 12f, MutedText);
        Place(_selectedInfo.rectTransform, 64f, y + 22f, 300f, 20f);

        var qx = 372f;
        var minus = AddButton(_panel, "Minus", "-", ButtonColor, () => NudgeQuantity(-1));
        Place(Rt(minus), qx, y + 8f, 26f, 28f);
        _quantity = AddInputField(_panel, "Quantity", "1", TMP_InputField.ContentType.IntegerNumber);
        _quantity.characterLimit = 7;
        _quantity.text = "1";
        _quantity.onEndEdit.AddListener(new Action<string>(value =>
        {
            int.TryParse(value, out var typed);
            var clamped = Math.Clamp(typed, 1, MaxQuantityForSelected).ToString();
            if (clamped != value) _quantity.text = clamped;
        }));
        Place(Rt(_quantity), qx + 28f, y + 8f, 50f, 28f);
        // Currency allows 999,999: tighten the padding and shrink the digits so six fit without "999...".
        Stretch(_quantity.textViewport, 3f, 2f, 3f, 2f);
        _quantity.onValueChanged.AddListener(new Action<string>(value =>
            _quantity.pointSize = (value?.Length ?? 0) > 4 ? 11.5f : 14f));
        var plus = AddButton(_panel, "Plus", "+", ButtonColor, () => NudgeQuantity(1));
        Place(Rt(plus), qx + 80f, y + 8f, 26f, 28f);
        var max = AddButton(_panel, "Max", "Max", ButtonColor, SetMaxQuantity);
        Place(Rt(max), qx + 108f, y + 8f, 40f, 28f);

        _addButton = AddButton(_panel, "Add", "Add", AccentColor, AddSelected);
        var addX = qx + 152f;
        Place(Rt(_addButton), addX, y + 4f, Width - Margin - addX, 36f);
        _addLabel = _addButton.GetComponentInChildren<TextMeshProUGUI>();
        if (_skinned)
            foreach (var b in new[] { minus, plus, max, _addButton })
                SetButtonLook(b.GetComponent<Image>(), NavyFill);
        _addLabel.fontSize = 15f;

        // Corruption row (gear: corruptionLevel; corruption stones: their level).
        var cy = y + 46f;
        _corruptionLabel = AddText(_panel, "CorruptionLabel", "Corruption", 13f, new Color(0.75f, 0.52f, 0.99f, 1f));
        Place(_corruptionLabel.rectTransform, qx - 96f, cy, 92f, 28f);
        _corruptionLabel.alignment = TextAlignmentOptions.MidlineRight;
        _corruptionMinus = AddButton(_panel, "CorruptionMinus", "-", ButtonColor, () => NudgeCorruption(-1));
        Place(Rt(_corruptionMinus), qx, cy, 26f, 28f);
        var valueBox = Place(NewRect(_panel, "CorruptionValueBox"), qx + 28f, cy, 50f, 28f);
        AddImage(valueBox.gameObject, FieldColor, false);
        _corruptionValue = AddText(valueBox, "Value", "+0", 14f, Color.white);
        _corruptionValue.alignment = TextAlignmentOptions.Center;
        Stretch(_corruptionValue.rectTransform, 0f, 0f, 0f, 0f);
        _corruptionPlus = AddButton(_panel, "CorruptionPlus", "+", ButtonColor, () => NudgeCorruption(1));
        Place(Rt(_corruptionPlus), qx + 80f, cy, 26f, 28f);
        _corruptionMax = AddButton(_panel, "CorruptionMax", "Max", ButtonColor, () => SetCorruption(ItemDetailsIndex.MaxCorruptionLevel));
        Place(Rt(_corruptionMax), qx + 108f, cy, 40f, 28f);
        var zero = AddButton(_panel, "CorruptionZero", "None", ButtonColor, () => SetCorruption(0));
        if (_skinned)
            foreach (var b in new[] { _corruptionMinus, _corruptionPlus, _corruptionMax, zero })
                SetButtonLook(b.GetComponent<Image>(), CorruptFill);
        Place(Rt(zero), qx + 152f, cy, 52f, 28f);

        // Extra stats row: stats added to each spawned copy of gear (stored per copy in the save).
        var ey = cy + 34f;
        _extraLabel = AddText(_panel, "ExtraLabel", "Item stats", 13f, new Color(0.95f, 0.80f, 0.45f, 1f));
        const float ex = 196f;
        Place(_extraLabel.rectTransform, Margin + 52f, ey, ex - Margin - 56f, 28f);
        _extraLabel.alignment = TextAlignmentOptions.MidlineRight;
        _extraStatButton = AddButton(_panel, "ExtraStat", string.Empty, ButtonColor, () => ToggleDropdown(_extraDropdown));
        Place(Rt(_extraStatButton), ex, ey, 120f, 28f);
        _extraStatLabel = _extraStatButton.GetComponentInChildren<TextMeshProUGUI>();
        _extraStatLabel.fontSize = 12f;
        _extraValue = AddInputField(_panel, "ExtraValue", "100", TMP_InputField.ContentType.DecimalNumber);
        _extraValue.characterLimit = 8;
        _extraValue.text = "100";
        Place(Rt(_extraValue), ex + 124f, ey, 58f, 28f);
        _extraAdd = AddButton(_panel, "ExtraAdd", "+ Add", ButtonColor, AddExtraStat);
        Place(Rt(_extraAdd), ex + 186f, ey, 52f, 28f);
        _extraClear = AddButton(_panel, "ExtraClear", "Clear", ButtonColor, ClearExtraStats);
        _extraGodly = AddButton(_panel, "ExtraGodly", "Godly", WarnColor, AddGodlyStats);
        Place(Rt(_extraGodly), ex + 242f, ey, 58f, 28f);
        if (_skinned) SetButtonLook(_extraGodly.GetComponent<Image>(), GreenFill);
        _extraApply = AddButton(_panel, "ExtraApply", "Apply", AccentColor, ApplyItemOverride);
        Place(Rt(_extraApply), ex + 304f, ey, 62f, 28f);
        if (_skinned) SetButtonLook(_extraApply.GetComponent<Image>(), RedFill);
        Place(Rt(_extraClear), ex + 370f, ey, Width - Margin - (ex + 370f), 28f);
        _extraDropdownX = ex;
        _extraDropdownY = ey + 30f;
        _extraStatIndex = Math.Max(0, Array.FindIndex(CatalogProbeState.SortableStats, s => s.Name.Equals("Strength", StringComparison.OrdinalIgnoreCase)));
        UpdateExtraStatLabel();

        _status = AddText(_panel, "Status", string.Empty, 12f, MutedText);
        Place(_status.rectTransform, Margin, Height - 50f, Width - 2f * Margin - 170f, 24f);
        BuildCredit();

        var quest = AddButton(_panel, "QuestToggle", string.Empty, ButtonColor, ToggleQuestItems);
        Place(Rt(quest), Width - Margin - 164f, Height - 50f, 164f, 24f);
        _questButton = quest.GetComponent<Image>();
        _questLabel = quest.GetComponentInChildren<TextMeshProUGUI>();
        _questLabel.fontSize = 12f;
    }

    /// <summary>Side panel: large item art, stats (with the chosen corruption applied) and sources.</summary>
    private void BuildPreview()
    {
        var preview = NewRect(_panel, "Preview");
        preview.anchorMin = preview.anchorMax = new Vector2(1f, 1f);
        preview.pivot = new Vector2(0f, 1f);
        preview.anchoredPosition = new Vector2(PreviewGap, 0f);
        preview.sizeDelta = new Vector2(PreviewWidth, Height);
        SkinWindow(preview, AddImage(preview.gameObject, PanelColor, true));

        var header = Place(NewRect(preview, "Header"), 0f, 0f, PreviewWidth, 32f);
        SkinHeader(AddImage(header.gameObject, HeaderColor, false));
        var title = AddText(header, "Title", "Item preview", 16f, _skinned ? new Color(1f, 0.86f, 0.52f, 1f) : Color.white);
        // Same regular list-style font as the rest of the window (user request).
        Place(title.rectTransform, Margin, 0f, PreviewWidth - 2f * Margin, 32f);

        var frame = Place(NewRect(preview, "BigFrame"), Margin, 44f, 104f, 104f);
        _previewFrame = AddImage(frame.gameObject, FieldColor, false);
        var icon = NewRect(frame, "BigIcon");
        Stretch(icon, 6f, 6f, 6f, 6f);
        _previewIcon = AddImage(icon.gameObject, Color.white, false);
        _previewIcon.preserveAspect = true;

        _previewName = AddText(preview, "Name", string.Empty, 18f, Color.white);
        _previewName.fontStyle = FontStyles.Normal; // same regular weight as the list
        _previewName.textWrappingMode = TextWrappingModes.Normal;
        _previewName.overflowMode = TextOverflowModes.Ellipsis;
        _previewName.alignment = TextAlignmentOptions.TopLeft;
        Place(_previewName.rectTransform, 128f, 46f, PreviewWidth - 128f - Margin, 100f);

        var area = Place(NewRect(preview, "BodyScroll"), Margin, 158f, PreviewWidth - 2f * Margin, Height - 158f - Margin);
        AddImage(area.gameObject, FieldColor, true);
        _previewScroll = area.gameObject.AddComponent<ScrollRect>();
        var viewport = NewRect(area, "Viewport");
        Stretch(viewport, 8f, 6f, 8f, 6f);
        viewport.gameObject.AddComponent<RectMask2D>();
        AddImage(viewport.gameObject, new Color(0f, 0f, 0f, 0.01f), true);
        _previewContent = NewRect(viewport, "Content");
        _previewContent.anchorMin = new Vector2(0f, 1f);
        _previewContent.anchorMax = new Vector2(1f, 1f);
        _previewContent.pivot = new Vector2(0.5f, 1f);
        _previewContent.sizeDelta = Vector2.zero;
        _previewBody = AddText(_previewContent, "Body", string.Empty, 14f, new Color(0.88f, 0.92f, 0.90f, 1f));
        // The game's clean sans-serif UI font reads better for dense stat blocks than the serif title font.
        // Same clean font as the rest of the window (bundled Inter unless the user picked a game font).
        _previewBody.lineSpacing = 6f;
        _previewBody.textWrappingMode = TextWrappingModes.Normal;
        _previewBody.overflowMode = TextOverflowModes.Overflow;
        _previewBody.alignment = TextAlignmentOptions.TopLeft;
        _previewBody.richText = true;
        Stretch(_previewBody.rectTransform, 0f, 0f, 0f, 0f);
        _previewScroll.content = _previewContent;
        _previewScroll.viewport = viewport;
        _previewScroll.horizontal = false;
        _previewScroll.movementType = ScrollRect.MovementType.Clamped;
        _previewScroll.inertia = false;
        _previewScroll.scrollSensitivity = 40f;
    }

    private void RefreshPreview()
    {
        if (_previewBody is null) return;
        if (_selected < 0)
        {
            _previewName.text = "Nothing selected";
            _previewName.color = MutedText;
            _previewIcon.color = Transparent;
            _previewFrame.sprite = null;
            _previewFrame.color = FieldColor;
            SetPreviewBody("Click an item in the list to see its stats, where it comes from and its description.");
            return;
        }

        var i = _selected;
        var id = CatalogProbeState.ItemIds[i];
        var color = CatalogProbeState.ItemRarityColors[i];
        _previewName.text = $"{CatalogProbeState.ItemNames[i]}\n<size=13><color=#{ColorUtility.ToHtmlStringRGB(color)}>{CatalogProbeState.ItemRarities[i]}</color>"
            + $"<color=#9aa3ad>{_sep}ID {id}{_sep}stack {Math.Max(1, CatalogProbeState.ItemStackLimits?[i] ?? 1)}</color></size>";
        _previewName.color = Brighten(color);
        _previewIcon.sprite = _rowIcons[i].sprite;
        _previewIcon.color = _rowIcons[i].sprite is null ? Transparent : Color.white;
        _previewFrame.sprite = _rows[i].transform.Find("Frame")?.GetComponent<Image>()?.sprite;
        _previewFrame.color = _previewFrame.sprite is null ? Darken(color) : Color.white;

        string body;
        try
        {
            var item = GameDatabase.Instance.GetItems()[id];
            var level = CorruptionApplies(i) && !ItemDetailsIndex.IsCorruptionToken(id) ? _corruption : 0;
            var sb = new System.Text.StringBuilder();
            var category = CatalogProbeState.ItemCategories[i];
            sb.Append(ItemDetailsIndex.StatsText(item, level, _bullet, category == "Armor" || category == "Equipment"));
            if (ItemDetailsIndex.IsCorruptionToken(id))
                sb.Append($"\n<color=#c084fc>Corruption stone: spawns at level +{_corruption} (set it below the quantity).</color>");
            if (_extras.Count > 0 && CorruptionApplies(i) && !ItemDetailsIndex.IsCorruptionToken(id))
                sb.Append($"\n<color=#7cfc9a>Pending (press Apply): {ExtrasText()}</color>");
            if (ItemOverrides.Has(id))
                sb.Append("\n<color=#7cfc9a>Stat override active: applies to every copy while the mod is installed (Clear restores).</color>");
            var usable = CatalogProbeState.ItemClassText?[i];
            // Only worth showing when the game actually restricts the item to some classes.
            if (!string.IsNullOrEmpty(usable) && usable != "All classes")
                sb.Append($"\n<color=#7fb8e6>Usable by: {usable}</color>");
            var onUse = ItemDetailsIndex.OnUseText(item, _bullet);
            if (!string.IsNullOrEmpty(onUse))
            {
                var factor = ItemOverrides.MultiplierOf(id);
                sb.Append("\n\n<b><color=#f2cf74>On use</color></b>").Append(factor > 1 ? $" <color=#7cfc9a>(x{factor} override)</color>" : string.Empty).Append('\n').Append(onUse);
            }

            sb.Append("\n\n<b><color=#f2cf74>Where it comes from</color></b>\n");
            sb.Append(ItemDetailsIndex.SourcesText(id, item.dropInWorld, _bullet));
            if (CatalogProbeState.ItemDevNames?[i] == true) sb.Append("\n<color=#f0b84d>Name marks a developer/test record.</color>");
            var description = item.description;
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append("\n\n<b><color=#f2cf74>Description</color></b>\n<i>").Append(description.Trim()).Append("</i>");
            body = sb.ToString();
        }
        catch (Exception exception)
        {
            body = $"Details unavailable: {exception.GetType().Name}.";
            CatalogProbeState.Log?.LogWarning($"Preview for item {id} failed: {exception}");
        }

        SetPreviewBody(body);
    }

    private void SetPreviewBody(string text)
    {
        // One look everywhere: the list font at regular weight, so drop bold/italic/small-caps tags
        // (colours and sizes stay).
        text = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, "</?(b|i|smallcaps)>", string.Empty);
        _previewBody.text = text;
        var width = PreviewWidth - 2f * Margin - 16f;
        var height = _previewBody.GetPreferredValues(text, width, 0f).y + 12f;
        _previewContent.sizeDelta = new Vector2(0f, height);
        _previewScroll.verticalNormalizedPosition = 1f;
    }

    // ---------- behaviour ----------

    private void ApplyFilter()
    {
        var names = CatalogProbeState.ItemNames;
        var ids = CatalogProbeState.ItemIds;
        var types = CatalogProbeState.ItemTypes;
        var categories = CatalogProbeState.ItemCategories;
        var rarities = CatalogProbeState.ItemRarities;
        var prices = CatalogProbeState.ItemSellPrices;
        var sources = CatalogProbeState.ItemSources;
        var tokens = _query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        _visible.Clear();
        for (var i = 0; i < ids.Length; i++)
        {
            var inCategory = _category switch
            {
                "All" => true,
                HiddenCategory => IsHidden(i),
                _ => categories[i] == _category
            };
            var index = i;
            var classSets = CatalogProbeState.ItemClassSets;
            var classOk = _classFilter < 0 || classSets is null || classSets[i] is null
                || classSets[i].Contains(ItemClassIndex.Classes[_classFilter].Id);
            var classText = CatalogProbeState.ItemClassText;
            var match = inCategory && classOk && tokens.All(t => names[index].Contains(t, StringComparison.OrdinalIgnoreCase)
                || (classText?[index]?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || ids[index].ToString() == t
                || types[index].Contains(t, StringComparison.OrdinalIgnoreCase)
                || (rarities[index]?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (sources?[index]?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
            if (match) _visible.Add(i);
            else _rows[i].SetActive(false);
        }

        var levels = CatalogProbeState.ItemLevels;
        var stats = CatalogProbeState.ItemStatMaps;
        var powerId = CatalogProbeState.ItemPowerStatId;
        var classId = _classFilter >= 0 && _classFilter < ItemClassIndex.Classes.Length ? ItemClassIndex.Classes[_classFilter].Id : -1;
        float Stat(int i, int statId) => statId >= 0 && stats?[i] is { } m && m.TryGetValue(statId, out var v) ? v : 0f;
        float Stats(int i, int[] statIds)
        {
            if (statIds is null) return 0f;
            var sum = 0f;
            foreach (var id in statIds) sum += Stat(i, id);
            return sum;
        }

        // Class score is computed once per visible item (not per comparison).
        var classScore = new Dictionary<int, float>();
        if (classId >= 0)
            foreach (var i in _visible) classScore[i] = ItemClassIndex.ScoreFor(classId, i, stats?[i]);

        // "Best": rarity, then (with a class selected) how much of that class's stats the item
        // gives, then the game's Item power, level requirement and value.
        int Best(int a, int b)
        {
            var c = RarityRank(rarities[b]).CompareTo(RarityRank(rarities[a]));
            if (c != 0) return c;
            if (classId >= 0)
            {
                c = classScore[b].CompareTo(classScore[a]);
                if (c != 0) return c;
            }

            c = Stat(b, powerId).CompareTo(Stat(a, powerId));
            if (c != 0) return c;
            c = (levels?[b] ?? 0).CompareTo(levels?[a] ?? 0);
            if (c != 0) return c;
            c = (prices?[b] ?? 0).CompareTo(prices?[a] ?? 0);
            if (c != 0) return c;
            c = string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : ids[a].CompareTo(ids[b]);
        }

        var option = _sortOptions.Count > 0 ? _sortOptions[Math.Clamp(_sortMode, 0, _sortOptions.Count - 1)] : ("Best", SortBest, (int[])null);
        _visible.Sort(option.Item2 switch
        {
            SortLevel => (a, b) => { var c = (levels?[b] ?? 0).CompareTo(levels?[a] ?? 0); return c != 0 ? c : Best(a, b); },
            SortName => (a, b) => { var c = string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase); return c != 0 ? c : ids[a].CompareTo(ids[b]); },
            SortId => (a, b) => ids[a].CompareTo(ids[b]),
            SortStat => (a, b) => { var c = Stats(b, option.Item3).CompareTo(Stats(a, option.Item3)); return c != 0 ? c : Best(a, b); },
            _ => Best
        });

        for (var position = 0; position < _visible.Count; position++)
        {
            var row = _rows[_visible[position]];
            row.SetActive(true);
            _rowRects[_visible[position]].anchoredPosition = new Vector2(0f, -position * RowHeight);
        }

        _content.sizeDelta = new Vector2(0f, _visible.Count * RowHeight);
        _scroll.verticalNormalizedPosition = 1f;
        _thumb.SetValueWithoutNotify(1f);
        _thumb.gameObject.SetActive(_visible.Count * RowHeight > Height - ListTop - FooterHeight);
        foreach (var pair in _categoryButtons)
            SetToggleLook(pair.Value, pair.Key == _category, AccentColor, NavyFill);
        UpdatePositionLabel();
    }

    private void UpdatePositionLabel()
    {
        var total = _visible.Count;
        if (total == 0)
        {
            _countLabel.text = "No matches";
            return;
        }

        var viewHeight = Height - ListTop - FooterHeight - 4f;
        var first = Math.Clamp((int)(_content.anchoredPosition.y / RowHeight) + 1, 1, total);
        var last = Math.Clamp(first + (int)(viewHeight / RowHeight) - 1, first, total);
        _countLabel.text = $"{first}-{last} of {total}";
    }

    private string ClassLabel() => _classFilter < 0 || _classFilter >= ItemClassIndex.Classes.Length
        ? "Class: Any" : $"Class: {ItemClassIndex.Classes[_classFilter].Name}";

    private void PickClass(int option)
    {
        // Option 0 = any class; option n = ItemClassIndex.Classes[n - 1].
        _classFilter = option - 1;
        MarkDropdown(_classDropdown, option);
        _classLabel.text = ClassLabel();
        SetToggleLook(_classButton, _classFilter >= 0, AccentColor, NavyFill);
        ApplyFilter();
    }

    private string SortLabel() => $"Sort: {(_sortOptions.Count > 0 ? _sortOptions[_sortMode].Label : "Best")}";

    private void PickSort(int option)
    {
        _sortMode = Math.Clamp(option, 0, Math.Max(0, _sortOptions.Count - 1));
        MarkDropdown(_sortDropdown, _sortMode);
        _sortLabel.text = SortLabel();
        ApplyFilter();
    }

    private void BuildSortOptions()
    {
        _sortOptions.Clear();
        _sortOptions.Add(("Best", SortBest, null));
        _sortOptions.Add(("Level", SortLevel, null));
        if (CatalogProbeState.ItemPowerStatId >= 0) _sortOptions.Add(("Item power", SortStat, new[] { CatalogProbeState.ItemPowerStatId }));
        _sortOptions.Add(("Name", SortName, null));
        _sortOptions.Add(("ID", SortId, null));
        foreach (var (ids, name) in CatalogProbeState.SortableStats)
            if (!(ids.Length == 1 && ids[0] == CatalogProbeState.ItemPowerStatId)) _sortOptions.Add((name, SortStat, ids));
    }

    // ---------- dropdowns ----------

    private void BuildDropdowns()
    {
        _sortDropdown = BuildDropdown("SortDropdown", _sortOptions.Select(o => o.Label).ToList(), PickSort, _sortDropdownX, 180f);
        var classLabels = new List<string> { "Any class" };
        classLabels.AddRange(ItemClassIndex.Classes.Select(c => c.Name));
        _classDropdown = BuildDropdown("ClassDropdown", classLabels, PickClass, _classDropdownX, 160f);
        var keepY = _dropdownY;
        _dropdownY = _extraDropdownY - 330f - 30f; // open upward from the footer row
        _extraDropdown = BuildDropdown("ExtraStatDropdown", CatalogProbeState.SortableStats.Select(s => s.Name).ToList(), PickExtraStat, _extraDropdownX, 200f);
        _dropdownY = keepY;
        MarkDropdown(_extraDropdown, _extraStatIndex);
    }

    /// <summary>
    /// A simple pop-up list: a screen-sized transparent blocker (click outside closes it) plus a
    /// scrollable column of buttons. Built once, toggled with SetActive.
    /// </summary>
    private readonly Dictionary<IntPtr, List<TextMeshProUGUI>> _dropdownLabels = new();

    /// <summary>Marks the current choice of a dropdown in gold.</summary>
    private void MarkDropdown(GameObject dropdown, int chosen)
    {
        if (dropdown is null || !_dropdownLabels.TryGetValue(dropdown.Pointer, out var labels)) return;
        for (var n = 0; n < labels.Count; n++) labels[n].color = n == chosen ? Gold : Color.white;
    }

    private GameObject BuildDropdown(string name, IList<string> labels, Action<int> onPick, float x, float width)
    {
        var optionLabels = new List<TextMeshProUGUI>();
        var root = NewRect(_panel, name);
        Stretch(root, 0f, 0f, 0f, 0f);
        var blocker = NewRect(root, "Blocker");
        blocker.anchorMin = blocker.anchorMax = new Vector2(0.5f, 0.5f);
        blocker.sizeDelta = new Vector2(8000f, 8000f);
        AddImage(blocker.gameObject, new Color(0f, 0f, 0f, 0.001f), true);
        var close = blocker.gameObject.AddComponent<Button>();
        close.onClick.AddListener(new Action(() => root.gameObject.SetActive(false)));

        const float itemHeight = 24f;
        var height = Math.Min(labels.Count * itemHeight + 8f, 330f);
        var box = Place(NewRect(root, "Box"), x, _dropdownY, width, height);
        AddImage(box.gameObject, new Color(0.08f, 0.09f, 0.12f, 0.99f), true);
        var scroll = box.gameObject.AddComponent<ScrollRect>();
        var viewport = NewRect(box, "Viewport");
        Stretch(viewport, 4f, 4f, 4f, 4f);
        viewport.gameObject.AddComponent<RectMask2D>();
        AddImage(viewport.gameObject, new Color(0f, 0f, 0f, 0.01f), true);
        var content = NewRect(viewport, "Content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, labels.Count * itemHeight);
        for (var n = 0; n < labels.Count; n++)
        {
            var index = n;
            var item = AddButton(content, "Option_" + n, labels[n], ButtonColor, () =>
            {
                root.gameObject.SetActive(false);
                onPick(index);
            }, plain: true);
            var rect = Rt(item);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, itemHeight - 2f);
            rect.anchoredPosition = new Vector2(0f, -n * itemHeight);
            var text = item.GetComponentInChildren<TextMeshProUGUI>();
            text.fontSize = 12f;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            optionLabels.Add(text);
        }

        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = itemHeight * 3f;
        root.gameObject.SetActive(false);
        _dropdownLabels[root.gameObject.Pointer] = optionLabels;
        if (optionLabels.Count > 0) optionLabels[0].color = Gold;
        return root.gameObject;
    }

    private void ToggleDropdown(GameObject dropdown)
    {
        if (dropdown is null) return;
        var open = !dropdown.activeSelf;
        if (_sortDropdown is not null) _sortDropdown.SetActive(false);
        if (_classDropdown is not null) _classDropdown.SetActive(false);
        if (!open) return;
        dropdown.transform.SetAsLastSibling();
        dropdown.SetActive(true);
    }

    private void Select(int index)
    {
        if (_selected >= 0) _rowBackgrounds[_selected].color = RowTint(CatalogProbeState.ItemRarityColors[_selected], false);
        _selected = index;
        _rowBackgrounds[index].color = RowTint(CatalogProbeState.ItemRarityColors[index], true);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_selected < 0)
        {
            _selectedName.text = "Select an item";
            _selectedInfo.text = "Click a row, set a quantity, then Add.";
            _selectedIcon.color = Transparent;
            _status.text = string.Empty;
            RefreshAddButton();
            RefreshCorruption();
            RefreshPreview();
            return;
        }

        var i = _selected;
        var color = CatalogProbeState.ItemRarityColors[i];
        _selectedName.text = CatalogProbeState.ItemNames[i];
        _selectedName.color = Brighten(color);
        var stack = Math.Max(1, CatalogProbeState.ItemStackLimits?[i] ?? 1);
        var source = CatalogProbeState.ItemSources?[i];
        _selectedInfo.text = $"stack {stack}{_sep}"
            + (string.IsNullOrEmpty(source) ? "no listed source" : source);
        _selectedFrame.color = Darken(color);
        _selectedIcon.sprite = _rowIcons[i].sprite;
        _selectedIcon.color = _rowIcons[i].sprite is null ? Transparent : Color.white;
        _status.color = MutedText;
        _status.text = IsQuest(i)
            ? (QuestAllowed ? "Quest item: adding it can break quest progression." : "Quest item: blocked. Turn Quest Items On (bottom right) to allow.")
            : CatalogProbeState.ItemDevNames?[i] == true ? "Developer/test record: may be unfinished or behave oddly."
            : string.IsNullOrEmpty(source) ? "Not dropped, sold, crafted or rewarded anywhere the database lists."
            : string.Empty;
        RefreshAddButton();
        RefreshCorruption();
        RefreshPreview();
    }

    private void RefreshAddButton()
    {
        var inGame = SafeIsInGame();
        var blocked = _selected >= 0 && IsQuest(_selected) && !QuestAllowed;
        _addButton.interactable = _selected >= 0 && inGame && !blocked;
        _addLabel.text = !inGame ? "In-game only" : blocked ? "Blocked" : "Add";
    }

    private void RefreshQuestToggle()
    {
        _questLabel.text = QuestAllowed ? "Quest Items: On" : "Quest Items: Off";
        if (_skinned) SetButtonLook(_questButton, QuestAllowed ? GreenFill : RedFill); else SetToggleLook(_questButton, QuestAllowed, WarnColor, CyanFill);
    }

    private void ToggleQuestItems()
    {
        if (Plugin.AllowQuestItems is null) return;
        Plugin.AllowQuestItems.Value = !Plugin.AllowQuestItems.Value;
        RebuildPolicy();
        RefreshQuestToggle();
        RefreshSelection();
        CatalogProbeState.Log?.LogInfo($"Quest items {(QuestAllowed ? "allowed" : "blocked")} by menu toggle.");
    }

    private void RebuildPolicy()
    {
        var policy = new SpawnPolicy(CurrencyMaxQuantity, QuestAllowed); // per-item cap is enforced by the UI
        _grantService ??= new GameItemGrantService();
        _coordinator = new SpawnCoordinator(CatalogProbeState.CurrentCatalog, policy, _grantService);
    }

    private void AddSelected()
    {
        if (_selected < 0) return;
        if (!int.TryParse(_quantity.text, out var quantity) || quantity < 1)
        {
            quantity = 1;
            _quantity.text = "1";
        }

        quantity = Math.Min(quantity, MaxQuantityForSelected);
        _grantService.CorruptionLevel = CorruptionApplies(_selected) ? _corruption : 0;
        var request = new SpawnRequest(CatalogProbeState.ItemIds[_selected], quantity);
        var result = _coordinator.Execute(request);
        _status.text = result.Message;
        _status.color = result.Succeeded ? new Color(0.55f, 0.85f, 0.55f, 1f) : new Color(0.95f, 0.55f, 0.45f, 1f);
        CatalogProbeState.Log?.LogInfo($"Spawn request {request.ItemId} x{request.Quantity}: {(result.Succeeded ? "OK" : "DENIED/FAILED")} - {result.Message}");
    }

    private void NudgeQuantity(int direction)
    {
        int.TryParse(_quantity.text, out var current);
        var step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 10 : 1;
        _quantity.text = Math.Clamp(current + direction * step, 1, MaxQuantityForSelected).ToString();
    }

    private void NudgeCorruption(int direction)
    {
        var step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 5 : 1;
        SetCorruption(_corruption + direction * step);
    }

    private void SetCorruption(int level)
    {
        _corruption = Math.Clamp(level, 0, ItemDetailsIndex.MaxCorruptionLevel);
        RefreshCorruption();
        RefreshPreview();
    }

    /// <summary>Corruption is meaningful for non-stackable gear and for corruption stones.</summary>
    private static bool CorruptionApplies(int i)
    {
        if (i < 0) return false;
        if (ItemDetailsIndex.IsCorruptionToken(CatalogProbeState.ItemIds[i])) return true;
        var category = CatalogProbeState.ItemCategories[i];
        var stack = CatalogProbeState.ItemStackLimits?[i] ?? 1;
        return stack <= 1 && (category == "Weapon" || category == "Armor" || category == "Equipment");
    }

    // ---------- extra stats ----------

    private TextMeshProUGUI _extraLabel;
    private Button _extraStatButton;
    private TextMeshProUGUI _extraStatLabel;
    private TMP_InputField _extraValue;
    private Button _extraAdd;
    private Button _extraClear;
    private Button _extraGodly;
    private Button _extraApply;
    private GameObject _extraDropdown;
    private float _extraDropdownX;
    private float _extraDropdownY;
    private int _extraStatIndex;
    private readonly List<(int StatId, float Value, string Name)> _extras = new();
    private bool _pendingLevelOne;

    /// <summary>True for non-gear items that do something when used (GameActions), e.g. renown rewards.</summary>
    private static bool HasUseEffect(int i)
    {
        if (i < 0 || CorruptionApplies(i)) return false;
        try
        {
            var item = GameDatabase.Instance.GetItems()[CatalogProbeState.ItemIds[i]];
            var actions = item.UseGameActionsTemplate && item.GameActionsTemplate is not null ? item.GameActionsTemplate.GameActions : item.GameActions;
            if (actions is null || item.UseGameActionsTemplate) return false;
            foreach (var action in actions) if (ItemOverrides.IsMultipliable(action)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateExtraStatLabel()
    {
        var stats = CatalogProbeState.SortableStats;
        _extraStatLabel.text = stats.Length == 0 ? "(no stats)" : stats[Math.Clamp(_extraStatIndex, 0, stats.Length - 1)].Name;
        _extraLabel.text = HasUseEffect(_selected) ? "Use effect x"
            : _extras.Count == 0 ? "Item stats" : $"Item stats ({_extras.Count} pending)";
    }

    private void PickExtraStat(int option)
    {
        _extraStatIndex = option;
        MarkDropdown(_extraDropdown, option);
        UpdateExtraStatLabel();
    }

    private void AddExtraStat()
    {
        var stats = CatalogProbeState.SortableStats;
        if (stats.Length == 0) return;
        if (!float.TryParse(_extraValue.text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value == 0f)
        {
            _status.text = "Enter a non-zero number for the extra stat.";
            return;
        }

        var stat = stats[Math.Clamp(_extraStatIndex, 0, stats.Length - 1)];
        // Respect the game's ceiling: a stat whose definition has a max (RPGStat.maxCheck/maxValue)
        // never gets more than that from this item (existing extras on the same stat count too).
        var note = string.Empty;
        var cap = Ceiling(stat.Ids[0], stat.Name);
        var already = _extras.Where(e => e.StatId == stat.Ids[0]).Sum(e => e.Value);
        var allowed = Math.Max(0f, cap - already);
        if (value > allowed)
        {
            value = allowed;
            note = $" (capped at {cap.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})";
        }

        if (value == 0f)
        {
            _status.text = $"{stat.Name} is already at the game maximum.";
            return;
        }

        _extras.Add((stat.Ids[0], value, stat.Name));
        UpdateExtraStatLabel();
        _status.color = MutedText;
        _status.text = $"Pending (press Apply): {ExtrasText()}{note}";
        RefreshPreview();
    }

    /// <summary>
    /// The most an item may add to a stat: the game's own cap (RPGStat.maxCheck/maxValue) when defined;
    /// otherwise practical limits from play: Movement Speed 45 (uncontrollable above that), percentage
    /// stats 100, everything else 10,000 (e.g. Intellect).
    /// </summary>
    private static float Ceiling(int statId, string name)
    {
        var cap = 10000f;
        if (CatalogProbeState.PercentStats.Contains(statId) || name.IndexOf("chance", StringComparison.OrdinalIgnoreCase) >= 0) cap = 100f;
        if (name.Equals("Movement Speed", StringComparison.OrdinalIgnoreCase)) cap = 45f;
        if (CatalogProbeState.StatCaps.TryGetValue(statId, out var gameCap) && gameCap > 0f) cap = Math.Min(cap, gameCap);
        return cap;
    }

    private static readonly string[] GodlyStats =
    {
        "Strength", "Agility", "Intellect", "Stamina", "Spirit", "Endurance", "Armor", "Magic Armor",
        "Healing Power", "Haste", "Critical Hit Chance", "Lifesteal", "Dodge chance", "Movement Speed", "Item power"
    };

    /// <summary>Adds every core stat at its ceiling (replacing any extras already chosen).</summary>
    private void AddGodlyStats()
    {
        if (HasUseEffect(_selected))
        {
            _extraValue.text = ItemOverrides.MaxMultiplier.ToString();
            _status.color = MutedText;
            _status.text = $"Use effect x{ItemOverrides.MaxMultiplier} - press Apply.";
            return;
        }

        _extras.Clear();
        foreach (var wanted in GodlyStats)
        {
            var match = CatalogProbeState.SortableStats.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match.Ids is null || match.Ids.Length == 0) continue;
            _extras.Add((match.Ids[0], Ceiling(match.Ids[0], match.Name), match.Name));
        }

        UpdateExtraStatLabel();
        _status.color = MutedText;
        _pendingLevelOne = true;
        _status.text = $"Godly: {_extras.Count} stats at their ceilings + usable at level 1 - press Apply.";
        RefreshPreview();
    }

    /// <summary>Writes the pending stats onto the selected item's definition (every copy, saved to the overrides file).</summary>
    private void ApplyItemOverride()
    {
        if (HasUseEffect(_selected))
        {
            int.TryParse(_extraValue.text, out var factor);
            factor = Math.Clamp(factor, 1, ItemOverrides.MaxMultiplier);
            var itemId = CatalogProbeState.ItemIds[_selected];
            ItemOverrides.SetMultiplier(itemId, factor);
            _status.color = new Color(0.55f, 0.85f, 0.55f, 1f);
            _status.text = factor > 1 ? $"{CatalogProbeState.ItemNames[_selected]}: use effect x{factor} (restore with Clear)." : "Use effect restored.";
            CatalogProbeState.Log?.LogInfo($"Use-effect multiplier x{factor} on item {itemId}.");
            RefreshPreview();
            return;
        }

        if (_selected < 0 || _extras.Count == 0) { _status.text = "Pick stats first (+ Add or Godly), then Apply."; return; }
        var id = CatalogProbeState.ItemIds[_selected];
        ItemOverrides.Set(id, _extras.Select(e => (e.StatId, e.Value)), _pendingLevelOne || ItemOverrides.IsLevelOne(id));
        _status.color = new Color(0.55f, 0.85f, 0.55f, 1f);
        _status.text = $"Applied {_extras.Count} stat(s) to every {CatalogProbeState.ItemNames[_selected]} (restore with Clear).";
        CatalogProbeState.Log?.LogInfo($"Stat override applied to item {id}: {ExtrasText()}.");
        _extras.Clear();
        _pendingLevelOne = false;
        CatalogProbeState.RefreshItemLevel(id);
        UpdateExtraStatLabel();
        RefreshPreview();
    }

    /// <summary>Clears pending stats; if none are pending, removes the selected item's override.</summary>
    private void ClearExtraStats()
    {
        if (HasUseEffect(_selected) && ItemOverrides.MultiplierOf(CatalogProbeState.ItemIds[_selected]) > 1)
        {
            ItemOverrides.SetMultiplier(CatalogProbeState.ItemIds[_selected], 1);
            _status.text = $"{CatalogProbeState.ItemNames[_selected]}: use effect restored.";
            UpdateExtraStatLabel();
            RefreshPreview();
            return;
        }

        if (_extras.Count > 0 || _pendingLevelOne)
        {
            _extras.Clear();
            _pendingLevelOne = false;
            _status.text = "Pending stats cleared.";
        }
        else if (_selected >= 0 && ItemOverrides.Has(CatalogProbeState.ItemIds[_selected]))
        {
            ItemOverrides.Clear(CatalogProbeState.ItemIds[_selected]);
            CatalogProbeState.RefreshItemLevel(CatalogProbeState.ItemIds[_selected]);
            _status.text = $"{CatalogProbeState.ItemNames[_selected]} restored to its original stats.";
            CatalogProbeState.Log?.LogInfo($"Stat override cleared for item {CatalogProbeState.ItemIds[_selected]}.");
        }

        UpdateExtraStatLabel();
        RefreshPreview();
    }

    private string ExtrasText() => string.Join(", ", _extras.Select(e => $"{(e.Value >= 0 ? "+" : string.Empty)}{e.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {e.Name}"));

    private void RefreshCorruption()
    {
        var applies = CorruptionApplies(_selected);
        var gear = applies && _selected >= 0 && !ItemDetailsIndex.IsCorruptionToken(CatalogProbeState.ItemIds[_selected]);
        if (_extraAdd is not null)
        {
            _extraAdd.interactable = gear;
            _extraStatButton.interactable = gear;
            var effect = HasUseEffect(_selected);
            _extraGodly.interactable = gear || effect;
            _extraApply.interactable = gear || effect;
            _extraClear.interactable = gear || effect;
            UpdateExtraStatLabel();
        }
        _corruptionMinus.interactable = applies;
        _corruptionPlus.interactable = applies;
        _corruptionMax.interactable = applies;
        _corruptionValue.text = applies ? $"+{_corruption}" : "n/a";
        _corruptionValue.color = applies && _corruption > 0 ? new Color(0.80f, 0.60f, 1f, 1f) : MutedText;
        _corruptionLabel.text = _selected >= 0 && ItemDetailsIndex.IsCorruptionToken(CatalogProbeState.ItemIds[_selected])
            ? "Stone level" : "Corruption";
    }

    private void SetMaxQuantity()
    {
        var stack = _selected >= 0 ? Math.Max(1, CatalogProbeState.ItemStackLimits?[_selected] ?? 1) : 1;
        _quantity.text = Math.Min(Math.Max(stack, CatalogProbeState.ItemCategories[_selected < 0 ? 0 : _selected] == "Currency" ? CurrencyMaxQuantity : 1), MaxQuantityForSelected).ToString();
    }

    private void HandleDrag()
    {
        var mouse = Input.mousePosition;
        if (Input.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(_header, new Vector2(mouse.x, mouse.y), null))
        {
            _dragging = true;
            _lastMouse = mouse;
        }

        if (_dragging && Input.GetMouseButton(0))
        {
            var delta = (mouse - _lastMouse) / _scale;
            _panel.anchoredPosition += new Vector2(delta.x, delta.y);
            _lastMouse = mouse;
            ClampPanelToScreen();
        }
        else if (_dragging)
        {
            _dragging = false;
            SaveWindowPosition();
        }
    }

    private static Vector2? LoadWindowPosition()
    {
        var raw = Plugin.WindowPosition?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length == 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
            return new Vector2(x, y);
        return null;
    }

    private void SaveWindowPosition()
    {
        if (Plugin.WindowPosition is null) return;
        var p = _panel.anchoredPosition;
        Plugin.WindowPosition.Value = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0},{1:0}", p.x, p.y);
    }

    /// <summary>Keeps at least the header on screen so the window can always be dragged back.</summary>
    private void ClampPanelToScreen()
    {
        var halfW = Screen.width / _scale / 2f;
        var halfH = Screen.height / _scale / 2f;
        var p = _panel.anchoredPosition;
        var minX = -halfW + Width / 2f;
        var maxX = Math.Max(minX, halfW - Width / 2f - PreviewGap - PreviewWidth);
        p.x = Math.Clamp(p.x, minX, maxX);
        p.y = Math.Clamp(p.y, -halfH - Height / 2f + 40f, halfH - Height / 2f);
        _panel.anchoredPosition = p;
    }

    private void UpdateTypingSignal()
    {
        var typing = _search.isFocused || _quantity.isFocused;
        if (typing == _typingSignalled) return;
        _typingSignalled = typing;
        var events = UIEvents.Instance;
        if (events is null) return;
        if (typing) events.OnPlayerTyping(); else events.OnPlayerStopTyping();
    }

    /// <summary>
    /// Safety net for late-bound sprite atlases: re-check rows whose icon was missing. Stops after
    /// five passes without change so it costs nothing once the catalog is settled.
    /// </summary>
    private void ResolveIcons()
    {
        var ids = CatalogProbeState.ItemIds;
        var items = GameDatabase.Instance?.GetItems();
        if (items is null) return;
        int ready = 0, missing = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            if (_iconReady[i])
            {
                ready++;
                continue;
            }

            try
            {
                var sprite = IconOf(items[ids[i]]);
                if (sprite is null)
                {
                    missing++;
                    continue;
                }

                _rowIcons[i].sprite = sprite;
                _rowIcons[i].color = Color.white;
                _iconReady[i] = true;
                ready++;
            }
            catch
            {
                missing++;
            }
        }

        if (ready != _lastReadyCount)
        {
            _lastReadyCount = ready;
            _iconPassesWithoutChange = 0;
            CatalogProbeState.Log?.LogInfo($"Item icons: {ready} drawable, {missing} without sprite.");
            if (_selected >= 0) RefreshSelection();
        }
        else
        {
            _iconPassesWithoutChange++;
        }
    }

    // ---------- item facts ----------

    private static int MaxQuantity => Math.Clamp(Plugin.MaximumQuantity?.Value ?? 999, 1, 9999);

    /// <summary>Currency items (e.g. Gold) may be added up to 999,999 at once; everything else uses MaximumQuantity.</summary>
    public const int CurrencyMaxQuantity = 999999;

    private int MaxQuantityForSelected =>
        _selected >= 0 && CatalogProbeState.ItemCategories[_selected] == "Currency" ? CurrencyMaxQuantity : MaxQuantity;

    private static bool QuestAllowed => Plugin.AllowQuestItems?.Value == true;

    private static bool IsQuest(int i) =>
        CatalogProbeState.ItemCategories[i] == "Quest" || CatalogProbeState.ItemProgressionFlags[i];

    private static bool IsHidden(int i) =>
        CatalogProbeState.ItemDevNames?[i] == true || string.IsNullOrEmpty(CatalogProbeState.ItemSources?[i]);

    private static string TagFor(int i) =>
        CatalogProbeState.ItemDevNames?[i] == true ? "DEV"
        : string.IsNullOrEmpty(CatalogProbeState.ItemSources?[i]) ? "NO SOURCE"
        : string.Empty;

    /// <summary>RPG Builder 2.x stores item art in entryIcon; the legacy icon field is empty in 0.16.3.</summary>
    private static Sprite IconOf(RPGItem item) => item is null ? null : (item.entryIcon ?? item.icon);

    private static bool SafeIsInGame()
    {
        try
        {
            return GameState.IsInGame();
        }
        catch
        {
            return false;
        }
    }

    private static int RarityRank(string rarity) => (rarity ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        // Labels observed at runtime in 0.16.3. "Gold" is used only by the Gold currency item (ID 30),
        // so it is not a gear tier and sorts with unranked entries.
        "legendary" => 5,
        "epic" => 4,
        "rare" => 3,
        "uncommon" => 2,
        "common" => 1,
        _ => 0
    };

    private static readonly Color Transparent = new(1f, 1f, 1f, 0f);

    private static Color RowTint(Color rarity, bool selected) =>
        new(rarity.r * 0.35f, rarity.g * 0.35f, rarity.b * 0.35f, selected ? 0.95f : 0.45f);

    private static Color Darken(Color c) => new(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f, 1f);

    private static Color Brighten(Color c) =>
        new(Math.Min(1f, c.r * 0.6f + 0.4f), Math.Min(1f, c.g * 0.6f + 0.4f), Math.Min(1f, c.b * 0.6f + 0.4f), 1f);

    // ---------- credit ----------

    private static readonly Color CreditYellow = new(1f, 0.85f, 0.25f, 1f);
    private Texture2D _alienTexture;
    private Sprite _alienSprite;

    /// <summary>"brought to you by soybean_alien" plus a small hand-drawn alien head, bottom-left.</summary>
    private void BuildCredit()
    {
        const string text = "- brought to you by soybean_alien";
        var label = AddText(_panel, "Credit", text, 12f, CreditYellow);
        label.fontStyle = FontStyles.Italic;
        label.overflowMode = TextOverflowModes.Overflow;
        var width = label.GetPreferredValues(text).x + 6f;
        Place(label.rectTransform, Margin + 2f, Height - 24f, width, 18f);
        try
        {
            _alienSprite ??= MakeAlienSprite();
            var icon = Place(NewRect(_panel, "CreditAlien"), Margin + 2f + width + 2f, Height - 27.5f, 17f, 17f);
            var image = AddImage(icon.gameObject, Color.white, false);
            image.sprite = _alienSprite;
            image.preserveAspect = true;
        }
        catch (Exception exception)
        {
            CatalogProbeState.Log?.LogWarning($"Credit icon unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Draws a small alien head into a texture at runtime (no image files): an almond-shaped green head
    /// that narrows to a chin, two large slanted black eyes with a tiny highlight, and soft edges.
    /// </summary>
    private Sprite MakeAlienSprite()
    {
        const int size = 64;
        var pixels = new Color[size * size];
        var green = new Color(0.47f, 0.93f, 0.36f, 1f);
        var rim = new Color(0.16f, 0.45f, 0.12f, 1f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size * 2f - 1f;          // -1..1, left to right
                var v = (y + 0.5f) / size * 2f - 1f;          // -1..1, bottom to top
                // Head: wide cranium, tapering to a pointed chin.
                var halfWidth = v >= 0.05f
                    ? 0.90f * MathF.Sqrt(Math.Max(0f, 1f - MathF.Pow((v - 0.05f) / 0.90f, 2f)))
                    : 0.90f * (1f + (v - 0.05f) * 0.82f);
                var inside = halfWidth - Math.Abs(u);
                if (v < -0.97f || inside <= -0.03f)
                {
                    pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                var alpha = Math.Clamp((inside + 0.03f) * size / 2f, 0f, 1f);
                var shade = 0.82f + 0.18f * Math.Clamp((v + 1f) / 2f, 0f, 1f);
                var color = inside < 0.06f ? rim : new Color(green.r * shade, green.g * shade, green.b * shade, 1f);

                // Eyes: rotated ellipses, outer corners raised.
                foreach (var side in new[] { -1f, 1f })
                {
                    var cx = 0.36f * side;
                    const float cy = -0.08f;
                    var angle = 0.55f * side;                 // radians; slant
                    var dx = u - cx;
                    var dy = v - cy;
                    var rx = dx * MathF.Cos(angle) + dy * MathF.Sin(angle);
                    var ry = -dx * MathF.Sin(angle) + dy * MathF.Cos(angle);
                    var e = rx * rx / (0.27f * 0.27f) + ry * ry / (0.14f * 0.14f);
                    if (e <= 1f)
                    {
                        color = new Color(0.03f, 0.03f, 0.04f, 1f);
                        var hx = u - (cx - 0.07f * side);
                        var hy = v - (cy + 0.05f);
                        if (hx * hx + hy * hy < 0.0036f) color = new Color(0.85f, 0.95f, 0.85f, 1f);
                    }
                }

                color.a = alpha;
                pixels[y * size + x] = color;
            }
        }

        _alienTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        _alienTexture.filterMode = FilterMode.Bilinear;
        _alienTexture.wrapMode = TextureWrapMode.Clamp;
        _alienTexture.hideFlags = HideFlags.HideAndDontSave;
        _alienTexture.SetPixels(pixels);
        _alienTexture.Apply();
        var sprite = Sprite.Create(_alienTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // ---------- skin: "alien glass" (all art drawn at runtime; no game or external images) ----------
    // Applies to the window/preview frames, header, buttons and input boxes. The item list and the
    // fonts are deliberately left as they were (user request).

    private bool _skinned;
    private static readonly Color Gold = new(0.91f, 0.78f, 0.42f, 1f);
    private static readonly Color Neon = new(0.96f, 0.80f, 0.42f, 1f);   // trim: gold
    private static readonly Color Cyan = new(0.45f, 0.70f, 1f, 1f);
    // Saturated fills for active/primary buttons (the glossy body lightens whatever tint it gets).
    private static readonly Color NeonFill = new(1.00f, 0.88f, 0.48f, 1f);   // active: bright polished gold
    private static readonly Color NavyFill = new(0.16f, 0.26f, 0.62f, 1f);   // quantity + Add: shiny navy
    private static readonly Color GreenFill = new(0.04f, 0.62f, 0.36f, 1f);  // emerald
    private static readonly Color RedFill = new(0.76f, 0.05f, 0.18f, 1f);    // ruby
    private static readonly Color CyanFill = new(1.00f, 0.94f, 0.74f, 1f);   // special: pale champagne gold
    private static readonly Color IdleBody = new(1.00f, 0.70f, 0.22f, 1f);   // idle: rich warm gold
    private static readonly Color CorruptFill = new(0.58f, 0.32f, 0.92f, 1f);   // corruption controls stay purple
    private static readonly Color DangerBody = new(0.50f, 0.11f, 0.13f, 1f);
    private static readonly Color LabelLight = new(1f, 0.95f, 0.82f, 1f);
    private static readonly Color LabelDark = new(0.13f, 0.08f, 0.02f, 1f);
    private static readonly Color DropdownRow = new(0.06f, 0.06f, 0.07f, 0.97f);
    private readonly Dictionary<IntPtr, TextMeshProUGUI> _buttonLabels = new();
    /// <summary>Skinned button parts keyed by the button's (transparent, clickable) root image.</summary>
    private readonly Dictionary<IntPtr, (Image Body, Image Glow)> _buttonParts = new();
    private Sprite _glowSprite;
    /// <summary>The game's own metallic button art (silver), tinted gold at runtime; null = procedural body.</summary>
    private Sprite _metalSprite;
    private Sprite _glossSprite;
    private Sprite _panelSprite;
    private Sprite _buttonBody;
    private Sprite _buttonRim;

    private void LoadSkin()
    {
        _skinned = Plugin.UseGameSkin?.Value != false;
        if (!_skinned) return;
        try
        {
            _panelSprite = MakePanelSprite();
            (_buttonBody, _buttonRim) = MakeButtonSprites();
            (_glowSprite, _glossSprite) = MakeShineSprites();
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite is not null && sprite.name == "Button_RL_Foreground" && false) { _metalSprite = sprite; break; } // disabled: looked muddy
            CatalogProbeState.Log?.LogInfo(_metalSprite is null ? "Skin: game metal button art not found; procedural gold." : "Skin: gold-tinted game metal buttons.");
            CatalogProbeState.Log?.LogInfo("Skin: alien glass (procedural).");
        }
        catch (Exception exception)
        {
            _skinned = false;
            CatalogProbeState.Log?.LogWarning($"Skin unavailable, using flat style: {exception.Message}");
        }
    }

    /// <summary>Signed distance to a rounded rectangle of half-size (hw, hh) centred at (cx, cy): &gt;0 inside.</summary>
    private static float RoundedInside(float px, float py, float cx, float cy, float hw, float hh, float radius)
    {
        var qx = Math.Max(Math.Abs(px - cx) - (hw - radius), 0f);
        var qy = Math.Max(Math.Abs(py - cy) - (hh - radius), 0f);
        var outside = MathF.Sqrt(qx * qx + qy * qy) - radius;
        var inner = Math.Min(Math.Max(Math.Abs(px - cx) - hw, Math.Abs(py - cy) - hh), 0f);
        return -(outside + inner);
    }

    /// <summary>Dark teal glass, rounded, thin neon edge and a soft outer glow.</summary>
    private static Sprite MakePanelSprite()
    {
        const int size = 64;
        const float pad = 6f, radius = 12f;
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = RoundedInside(x + 0.5f, y + 0.5f, size / 2f, size / 2f, size / 2f - pad, size / 2f - pad, radius);
            var v = (y + 0.5f) / size;
            Color c;
            if (d >= 1.6f)
            {
                var shade = 0.85f + 0.15f * v;
                c = new Color(0.060f, 0.070f, 0.090f, 0.98f);                    // navy, translucent
                if (d < 4f) c = Color.Lerp(new Color(0.45f, 0.34f, 0.10f, 0.55f), c, (d - 1.6f) / 2.4f); // inner rim glow
            }
            else if (d >= 0f)
            {
                c = new Color(Neon.r, Neon.g, Neon.b, 0.85f);                                            // neon edge
            }
            else
            {
                var glow = Math.Clamp(1f + d / pad, 0f, 1f);
                c = new Color(Neon.r, Neon.g, Neon.b, 0.28f * glow * glow);                             // outer glow
            }

            pixels[y * size + x] = c;
        }

        return MakeSlicedSprite(pixels, size, 22f);
    }

    /// <summary>Capsule body (light gradient, tinted per state) and a thin neon rim with a faint inner glow.</summary>
    private static (Sprite Body, Sprite Rim) MakeButtonSprites()
    {
        const int size = 32;
        const float radius = 2.5f; // near-square, crisp corners
        var body = new Color[size * size];
        var rim = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = RoundedInside(x + 0.5f, y + 0.5f, size / 2f, size / 2f, size / 2f - 0.5f, size / 2f - 0.5f, radius);
            var a = Math.Clamp(d + 0.5f, 0f, 1f);
            var v = (y + 0.5f) / size;
            // Bevel: strong top-to-bottom gradient, a bright top lip and a dark bottom lip (3D look).
            // Gold bar: darker belly, a sharp specular band near the top, a soft low reflection and crisp
            // bevel lips (bright top edge, dark bottom edge) - tinted gold by Image.color.
            var shade = 0.40f + 0.30f * v
                + 0.55f * MathF.Exp(-MathF.Pow((v - 0.74f) / 0.07f, 2f))
                + 0.14f * MathF.Exp(-MathF.Pow((v - 0.22f) / 0.10f, 2f));
            if (d < 2.2f && v > 0.5f) shade += 0.35f;
            if (d < 2.2f && v < 0.4f) shade *= 0.45f;
            shade = Math.Min(1f, shade);
            body[y * size + x] = new Color(shade, shade, shade, a);
            var line = d < 1.1f ? 1f : 0f; // crisp rim line, no fade
            rim[y * size + x] = new Color(Neon.r, Neon.g, Neon.b, Math.Min(a, line));
        }

        return (MakeSlicedSprite(body, size, 6f), MakeSlicedSprite(rim, size, 6f));
    }

    /// <summary>Soft rounded halo (for glow) and a white top-down gloss gradient (for shine).</summary>
    private static (Sprite Glow, Sprite Gloss) MakeShineSprites()
    {
        const int size = 48;
        const float pad = 10f, radius = 10f;
        var glow = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = RoundedInside(x + 0.5f, y + 0.5f, size / 2f, size / 2f, size / 2f - pad, size / 2f - pad, radius);
            var a = d >= 0f ? 1f : Math.Clamp(1f + d / pad, 0f, 1f);
            glow[y * size + x] = new Color(1f, 1f, 1f, a * a * (d >= 0f ? 0.55f : 1f));
        }

        const int gs = 32;
        var gloss = new Color[gs * gs];
        for (var y = 0; y < gs; y++)
        for (var x = 0; x < gs; x++)
        {
            var d = RoundedInside(x + 0.5f, y + 0.5f, gs / 2f, gs / 2f, gs / 2f - 0.5f, gs / 2f - 0.5f, 7f);
            var a = Math.Clamp(d + 0.5f, 0f, 1f);
            var v = (y + 0.5f) / gs;                       // 0 bottom .. 1 top
            var band = MathF.Exp(-MathF.Pow((v - 0.78f) / 0.09f, 2f));
            gloss[y * gs + x] = new Color(1f, 1f, 1f, a * (0.06f + 0.30f * v * v + 0.75f * band));
        }

        return (MakeSlicedSprite(glow, size, 20f), MakeSlicedSprite(gloss, gs, 9f));
    }

    private static Sprite MakeSlicedSprite(Color[] pixels, int size, float border)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.SetPixels(pixels);
        texture.Apply();
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u,
            SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private void SkinWindow(RectTransform window, Image background)
    {
        if (!_skinned) return;
        background.sprite = _panelSprite;
        background.type = Image.Type.Sliced;
        background.pixelsPerUnitMultiplier = 1f;
        background.color = Color.white;
    }

    private void SkinHeader(Image headerImage)
    {
        if (!_skinned) return;
        headerImage.color = new Color(0.045f, 0.052f, 0.068f, 1f);
        var line = NewRect(headerImage.rectTransform, "NeonLine");
        line.anchorMin = new Vector2(0f, 0f);
        line.anchorMax = new Vector2(1f, 0f);
        line.pivot = new Vector2(0.5f, 0f);
        line.sizeDelta = new Vector2(-16f, 1.5f);
        line.anchoredPosition = Vector2.zero;
        AddImage(line.gameObject, new Color(Neon.r, Neon.g, Neon.b, 0.8f), false);
    }

    private void SkinInput(TMP_InputField field, Image background)
    {
        if (!_skinned) return;
        background.sprite = _buttonBody;
        background.type = Image.Type.Sliced;
        background.color = new Color(0.03f, 0.03f, 0.035f, 0.92f);
        var rim = NewRect(Rt(field), "Rim");
        Stretch(rim, 0f, 0f, 0f, 0f);
        var rimImage = AddImage(rim.gameObject, new Color(1f, 1f, 1f, 0.55f), false);
        rimImage.sprite = _buttonRim;
        rimImage.type = Image.Type.Sliced;
    }

    /// <summary>Body colour for a skinned button, from its flat-style colour.</summary>
    private static Color ButtonBase(Color flat) =>
        flat == AccentColor ? NeonFill
        : flat == WarnColor ? CyanFill
        : flat == ButtonColor ? IdleBody
        : DangerBody;

    private static bool IsFilled(Color body) => body.r * 0.3f + body.g * 0.59f + body.b * 0.11f > 0.42f;

    /// <summary>Sets a skinned button's body colour and picks a readable label colour for it.</summary>
    private void SetButtonLook(Image image, Color body)
    {
        if (image is null) return;
        if (_buttonParts.TryGetValue(image.Pointer, out var parts))
        {
            parts.Body.color = body;
            var filled = IsFilled(body);
            parts.Glow.color = new Color(body.r, body.g, body.b, 0f); // no glow: crisp, shiny surfaces only
        }
        else
        {
            image.color = body;
        }

        if (_buttonLabels.TryGetValue(image.Pointer, out var label))
            label.color = Color.white; // one consistent style: white text, dark outline
    }

    /// <summary>Active/idle look for toggle-style buttons (tabs, class filter, quest toggle).</summary>
    private void SetToggleLook(Image image, bool active, Color flatActive, Color skinActive)
    {
        if (_skinned) SetButtonLook(image, active ? skinActive : IdleBody);
        else image.color = active ? flatActive : ButtonColor;
    }

    /// <summary>Visible "pressed" feedback: the button sinks (shrinks a little) and darkens while held.</summary>
    private static void AddPressEffect(Button button, RectTransform label = null, bool plain = false, Image glow = null)
    {
        var glowBase = 0f;
        var rect = Rt(button);
        var labelHome = label is null ? Vector2.zero : label.anchoredPosition;
        var trigger = button.gameObject.AddComponent<EventTrigger>();
        void On(EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(new Action<BaseEventData>(_ => action()));
            trigger.triggers.Add(entry);
        }

        void SetGlow(float factor)
        {
            if (glow is null) return;
            var c = glow.color;
            if (factor == 1f) glowBase = c.a; // remember the state's resting glow
            glow.color = new Color(c.r, c.g, c.b, Math.Clamp(glowBase * factor, 0f, 1f));
        }

        void Release()
        {
            rect.localScale = Vector3.one;
            if (label is not null) label.anchoredPosition = labelHome;
        }

        On(EventTriggerType.PointerEnter, () => { if (glow is not null) { glowBase = glow.color.a; SetGlow(2.2f); } });
        On(EventTriggerType.PointerDown, () =>
        {
            if (!plain) rect.localScale = new Vector3(0.94f, 0.90f, 1f);
            if (label is not null) label.anchoredPosition = labelHome + new Vector2(0f, -1.5f);
            SetGlow(0.6f);
        });
        On(EventTriggerType.PointerUp, () => { Release(); SetGlow(2.2f); });
        On(EventTriggerType.PointerExit, () => { Release(); SetGlow(1.0001f); });

        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = plain ? new Color(1.8f, 2.4f, 2.0f, 1f) : new Color(1.35f, 1.5f, 1.4f, 1f);
        colors.pressedColor = new Color(0.40f, 0.40f, 0.42f, 1f);
        colors.selectedColor = Color.white;
        colors.colorMultiplier = 2f;
        colors.fadeDuration = 0.07f;
        button.colors = colors;
    }

    // ---------- uGUI helpers ----------

    /// <summary>
    /// The RectTransform of a UI component. Never write (RectTransform)c.transform: Il2CppInterop
    /// returns whatever wrapper type its object cache holds for that pointer (often plain Transform),
    /// so a C# cast fails intermittently depending on GC timing. Cast&lt;T&gt;() re-wraps correctly.
    /// </summary>
    private static RectTransform Rt(Component component) => component.transform.Cast<RectTransform>();

    private static RectTransform NewRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    /// <summary>Top-left anchored placement in layout units (y grows downward).</summary>
    private static RectTransform Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static Image AddImage(GameObject go, Color color, bool raycast)
    {
        var image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    private TextMeshProUGUI AddText(Transform parent, string name, string text, float size, Color color)
    {
        var rect = NewRect(parent, name);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (_font is not null) label.font = _font;
        label.fontSize = size;
        label.color = color;
        label.text = text;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;
        if (_bundledFont) label.characterSpacing = -1.5f; // Geist set slightly tighter
        return label;
    }

    private Button AddButton(Transform parent, string name, string text, Color color, Action onClick, bool plain = false)
    {
        var rect = NewRect(parent, name);
        var image = AddImage(rect.gameObject, color, true);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);
        Image glowImage = null;
        if (_skinned && !plain)
        {
            // Layered "shiny 3D" button. The root image stays transparent (it only catches clicks) so
            // the shadow and glow can sit BEHIND the body: shadow, glow, body, gloss, rim, then label.
            image.color = new Color(0f, 0f, 0f, 0f);
            Image Layer(string layerName, Sprite sprite, Color c, float l, float top, float r, float b)
            {
                var layer = NewRect(rect, layerName);
                Stretch(layer, l, top, r, b);
                var img = AddImage(layer.gameObject, c, false);
                img.sprite = sprite;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 1.3f;
                return img;
            }

            // (no drop shadow: flat, hard-edged look)
            glowImage = Layer("Glow", _glowSprite, new Color(Neon.r, Neon.g, Neon.b, 0f), -7f, -7f, -7f, -7f);
            glowImage.pixelsPerUnitMultiplier = 1f;
            var body = Layer("Body", _metalSprite ?? _buttonBody, IdleBody, 0f, 0f, 0f, 0f);
            if (_metalSprite is not null)
            {
                body.pixelsPerUnitMultiplier = 3f; // shrink the big 536x150 art's borders to button size
            }
            else
            {
                var gloss = Layer("Gloss", _glossSprite, new Color(1f, 1f, 1f, 0.85f), 2f, 1.5f, 2f, 0f);
                gloss.rectTransform.anchorMin = new Vector2(0f, 0.45f);
            }
            // (no rim outline: crisp edges come from the body itself)
            button.targetGraphic = body;
            _buttonParts[image.Pointer] = (body, glowImage);
        }
        else if (_skinned)
        {
            image.color = DropdownRow;
        }

        var label = AddText(rect, "Label", text, 13f, _skinned ? LabelLight : Color.white);
        // Same face and weight as the item list (user request).
        label.alignment = TextAlignmentOptions.Center;
        Stretch(label.rectTransform, 2f, 0f, 2f, 0f);
        _buttonLabels[image.Pointer] = label;
        if (_skinned && !plain) SetButtonLook(image, ButtonBase(color));
        AddPressEffect(button, label.rectTransform, plain, glowImage);
        return button;
    }

    private TMP_InputField AddInputField(Transform parent, string name, string placeholder, TMP_InputField.ContentType contentType)
    {
        var rect = NewRect(parent, name);
        rect.gameObject.SetActive(false);
        var background = AddImage(rect.gameObject, FieldColor, true);
        var area = NewRect(rect, "Text Area");
        Stretch(area, 8f, 2f, 8f, 2f);
        area.gameObject.AddComponent<RectMask2D>();
        var hint = AddText(area, "Placeholder", placeholder, 14f, new Color(0.5f, 0.52f, 0.55f, 1f));
        Stretch(hint.rectTransform, 0f, 0f, 0f, 0f);
        var text = AddText(area, "Text", string.Empty, 14f, Color.white);
        Stretch(text.rectTransform, 0f, 0f, 0f, 0f);

        var field = rect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = background;
        field.textViewport = area;
        field.textComponent = text;
        field.placeholder = hint;
        if (_font is not null) field.fontAsset = _font;
        field.contentType = contentType;
        field.lineType = TMP_InputField.LineType.SingleLine;
        SkinInput(field, background);
        rect.gameObject.SetActive(true);
        return field;
    }
}
