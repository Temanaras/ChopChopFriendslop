using System.IO;
using ChopChop.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ChopChop.Editor
{
    /// <summary>
    /// Builds the game's canvas from code.
    ///
    /// Authored by a menu item for the same reason the placeholder trunk is
    /// (<see cref="PlaceholderTrunkGenerator"/>): a prefab is an opaque blob in review,
    /// and the numbers that decide where the health bar sits are worth being able to read
    /// in a diff. It also means the whole HUD can be rebuilt after a rename instead of
    /// being re-dragged.
    ///
    /// **Unlike the trunk, this refuses to overwrite.** A mesh has no hand edits worth
    /// keeping; a UI has nothing but. uGUI was chosen precisely so the layout can be
    /// nudged in the Scene view, so the plain menu item builds only what is missing and
    /// the destructive rebuild is a second, explicitly-named item.
    /// </summary>
    public static class UiBuilder
    {
        private const string Folder = "Assets/_Project/Runtime/UI/Prefabs";
        private const string RootPath = Folder + "/UiRoot.prefab";
        private const string SlotPath = Folder + "/ItemSlot.prefab";
        private const string ThemePath = "Assets/_Project/Runtime/UI/UiTheme.asset";

        /// <summary>Design resolution. The scaler matches width and height equally.</summary>
        private static readonly Vector2 Reference = new(1920f, 1080f);

        /// <summary>One inventory square, and the spacing between them.</summary>
        private const float Cell = 68f;
        private const float Gap = 8f;
        private const float WindowHeight = 520f;

        /// <summary>Diameter of the chop meter's circle at the design resolution.</summary>
        private const float MeterSize = 190f;

        [MenuItem("ChopChop/UI/Build Missing UI Assets")]
        public static void BuildMissing()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(RootPath) != null)
            {
                Debug.Log($"[UI] {RootPath} already exists; nothing built. " +
                          "Use ChopChop/UI/Rebuild UI Prefab (Discards Edits) to replace it.");
                EnsureTheme();
                return;
            }

            Build();
        }

        [MenuItem("ChopChop/UI/Rebuild UI Prefab (Discards Edits)")]
        public static void Rebuild()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(RootPath) != null
                && !EditorUtility.DisplayDialog(
                    "Rebuild UI prefab?",
                    $"This replaces {RootPath} with a freshly generated one.\n\n" +
                    "Every hand edit to the layout is discarded.",
                    "Rebuild", "Cancel"))
                return;

            Build();
        }

        private static void Build()
        {
            Directory.CreateDirectory(Folder);
            UiTheme theme = EnsureTheme();

            GameObject root = new("UiRoot", typeof(Canvas), typeof(CanvasScaler),
                                  typeof(GraphicRaycaster), typeof(UiRoot), typeof(HudController));

            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Reference;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            BuildEventSystem(root);

            GameObject hud = Child(root, "HUD", typeof(CanvasGroup));
            Stretch(hud);
            Set(root.GetComponent<HudController>(), "_hud", hud.GetComponent<CanvasGroup>());

            BuildCrosshair(hud);
            BuildVitals(hud, theme);
            BuildAmmo(hud, theme);
            BuildPrompt(hud, theme);
            BuildChopMeter(hud, theme);

            BuildInventory(root, theme, BuildSlotPrefab(theme));

            PrefabUtility.SaveAsPrefabAsset(root, RootPath);
            Object.DestroyImmediate(root);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[UI] Built {RootPath} at {Reference.x}x{Reference.y}.");
        }

        /* The stock module resolves its own default actions when none are assigned, which
         * would give the UI a second, invisible copy of the bindings. Pointing it at the
         * project-wide asset keeps one place where input is described. */
        private static void BuildEventSystem(GameObject root)
        {
            GameObject events = Child(root, "EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            InputSystemUIInputModule module = events.GetComponent<InputSystemUIInputModule>();

            InputActionAsset actions = InputSystem.actions;

            if (actions != null && actions.FindActionMap("UI") != null)
                module.actionsAsset = actions;
            else
                Debug.LogWarning("[UI] No UI action map in the project-wide actions asset; " +
                                 "the input module falls back to its own defaults.");
        }

        private static void BuildCrosshair(GameObject hud)
        {
            GameObject crosshair = Child(hud, "Crosshair", typeof(CanvasGroup), typeof(CrosshairView));
            Centre(crosshair, new Vector2(6f, 6f));

            GameObject dot = Child(crosshair, "Dot", typeof(Image));
            Stretch(dot);

            Image image = dot.GetComponent<Image>();
            image.sprite = Builtin("UI/Skin/Knob.psd");
            image.color = new Color(1f, 1f, 1f, 0.65f);
            image.raycastTarget = false;

            Set(crosshair.GetComponent<CrosshairView>(), "_group", crosshair.GetComponent<CanvasGroup>());
        }

        private static void BuildVitals(GameObject hud, UiTheme theme)
        {
            GameObject vitals = Child(hud, "Vitals", typeof(CanvasGroup), typeof(HealthBarView));

            // Bottom left, inset by a comfortable margin at the design resolution.
            RectTransform rect = (RectTransform)vitals.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(48f, 48f);
            rect.sizeDelta = new Vector2(320f, 26f);

            GameObject backing = Child(vitals, "Backing", typeof(Image));
            Stretch(backing);
            Image back = backing.GetComponent<Image>();
            back.sprite = Builtin("UI/Skin/UISprite.psd");
            back.type = Image.Type.Sliced;
            back.color = theme.HealthBacking;
            back.raycastTarget = false;

            GameObject fill = Child(vitals, "Fill", typeof(Image));
            Stretch(fill, 2f);
            Image bar = fill.GetComponent<Image>();
            bar.sprite = Builtin("UI/Skin/UISprite.psd");
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillOrigin = (int)Image.OriginHorizontal.Left;
            bar.fillAmount = 1f;
            bar.color = theme.Health;
            bar.raycastTarget = false;

            GameObject label = Child(vitals, "Label", typeof(TextMeshProUGUI));
            Stretch(label);
            TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
            Style(text, theme, 15f, TextAlignmentOptions.Right);
            text.margin = new Vector4(0f, 0f, 8f, 0f);
            text.text = "100 / 100";

            HealthBarView view = vitals.GetComponent<HealthBarView>();
            Set(view, "_fill", bar);
            Set(view, "_label", text);
            Set(view, "_group", vitals.GetComponent<CanvasGroup>());
            Set(view, "_theme", theme);
        }

        private static void BuildAmmo(GameObject hud, UiTheme theme)
        {
            GameObject ammo = Child(hud, "Ammo", typeof(CanvasGroup), typeof(AmmoView));

            // Bottom right, mirroring the health bar's margin.
            RectTransform rect = (RectTransform)ammo.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-48f, 48f);
            rect.sizeDelta = new Vector2(200f, 40f);

            GameObject label = Child(ammo, "Label", typeof(TextMeshProUGUI));
            Stretch(label);
            TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
            Style(text, theme, 28f, TextAlignmentOptions.Right);
            text.color = theme.Ammo;
            text.text = "0 / 0";

            AmmoView view = ammo.GetComponent<AmmoView>();
            Set(view, "_label", text);
            Set(view, "_group", ammo.GetComponent<CanvasGroup>());
        }

        private static void BuildPrompt(GameObject hud, UiTheme theme)
        {
            GameObject prompt = Child(hud, "Prompt", typeof(CanvasGroup), typeof(InteractionPromptView));

            /* 42% up the screen: under the crosshair rather than over it, which is where
             * the IMGUI prompt this replaces sat. Same number, same reason. */
            RectTransform rect = (RectTransform)prompt.transform;
            rect.anchorMin = new Vector2(0f, 0.42f);
            rect.anchorMax = new Vector2(1f, 0.42f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, 40f);

            GameObject label = Child(prompt, "Label", typeof(TextMeshProUGUI));
            Stretch(label);
            TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
            Style(text, theme, 20f, TextAlignmentOptions.Center);
            text.text = "[E]  Light the fire";

            InteractionPromptView view = prompt.GetComponent<InteractionPromptView>();
            Set(view, "_label", text);
            Set(view, "_group", prompt.GetComponent<CanvasGroup>());
        }

        /// <summary>
        /// One inventory square, saved as its own prefab so the screen can stamp out as
        /// many as the backpack has slots. Nothing about it knows which container it will
        /// end up in — that is set at runtime by the screen that instantiates it.
        /// </summary>
        /// <summary>
        /// The chopping timing game: a semicircle to the right of the crosshair with the
        /// pointer running up and down its curved edge.
        ///
        /// Every band is one radial-filled Image stacked on the same centre, drawn
        /// widest-first so the narrower ones sit on top — that stacking is what makes
        /// Perfect read as nested inside Good rather than as a separate wedge.
        /// </summary>
        private static void BuildChopMeter(GameObject hud, UiTheme theme)
        {
            GameObject meter = Child(hud, "ChopMeter", typeof(CanvasGroup), typeof(ChopMeterView));

            /* Beside the crosshair rather than under it. Chopping is aimed, and a meter
             * covering the trunk would put the thing being read on top of the thing being
             * hit. */
            RectTransform rect = (RectTransform)meter.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(190f, 0f);
            rect.sizeDelta = new Vector2(MeterSize, MeterSize);

            Image miss = Arc(meter, "MissHalf", theme.Miss);
            Image bad = Arc(meter, "BadBand", theme.Bad);
            Image good = Arc(meter, "GoodBand", theme.Good);
            Image perfect = Arc(meter, "PerfectBand", theme.Perfect);

            /* Punched out of the middle so the bands read as a gauge rather than a pie
             * chart. Drawn after the bands and before the pointer, so it hides their
             * inner ends without hiding the pointer riding the rim. */
            GameObject hub = Child(meter, "Hub", typeof(Image));
            Centre(hub, new Vector2(MeterSize * 0.52f, MeterSize * 0.52f));
            Image hubImage = hub.GetComponent<Image>();
            hubImage.sprite = Builtin("UI/Skin/Knob.psd");
            hubImage.color = new Color(0f, 0f, 0f, 0.55f);
            hubImage.raycastTarget = false;

            // The pointer is a pivot rotated about the centre, with the mark itself pushed
            // out to the rim — so rotating the parent sweeps it along the arc.
            GameObject pointer = Child(meter, "Pointer");
            Centre(pointer, new Vector2(MeterSize, MeterSize));

            GameObject mark = Child(pointer, "Mark", typeof(Image));
            RectTransform markRect = (RectTransform)mark.transform;
            markRect.anchorMin = markRect.anchorMax = new Vector2(1f, 0.5f);
            markRect.pivot = new Vector2(0.5f, 0.5f);
            markRect.anchoredPosition = Vector2.zero;
            markRect.sizeDelta = new Vector2(22f, 4f);
            Image markImage = mark.GetComponent<Image>();
            markImage.sprite = Builtin("UI/Skin/UISprite.psd");
            markImage.type = Image.Type.Sliced;
            markImage.color = theme.Text;
            markImage.raycastTarget = false;

            ChopMeterView view = meter.GetComponent<ChopMeterView>();
            Set(view, "_group", meter.GetComponent<CanvasGroup>());
            Set(view, "_pointer", (RectTransform)pointer.transform);
            Set(view, "_missHalf", miss);
            Set(view, "_badBand", bad);
            Set(view, "_goodBand", good);
            Set(view, "_perfectBand", perfect);
            Set(view, "_theme", theme);

            BuildChopFeedback(hud, theme);
        }

        /// <summary>One radial arc, filled clockwise from straight up.</summary>
        private static Image Arc(GameObject meter, string name, Color colour)
        {
            GameObject arc = Child(meter, name, typeof(Image));
            Stretch(arc);

            Image image = arc.GetComponent<Image>();
            image.sprite = Builtin("UI/Skin/Knob.psd");
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Top;
            image.fillClockwise = true;
            image.fillAmount = 0.5f;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private static void BuildChopFeedback(GameObject hud, UiTheme theme)
        {
            GameObject feedback = Child(hud, "ChopFeedback", typeof(CanvasGroup), typeof(ChopFeedbackView));
            Stretch(feedback);

            // Above the crosshair; the interaction prompt already owns the space below it.
            GameObject grade = Child(feedback, "Grade", typeof(TextMeshProUGUI));
            RectTransform gradeRect = (RectTransform)grade.transform;
            gradeRect.anchorMin = new Vector2(0f, 0.60f);
            gradeRect.anchorMax = new Vector2(1f, 0.60f);
            gradeRect.pivot = new Vector2(0.5f, 0.5f);
            gradeRect.anchoredPosition = Vector2.zero;
            gradeRect.sizeDelta = new Vector2(0f, 40f);
            TextMeshProUGUI gradeText = grade.GetComponent<TextMeshProUGUI>();
            Style(gradeText, theme, 26f, TextAlignmentOptions.Center);
            gradeText.fontStyle = FontStyles.Bold;
            gradeText.characterSpacing = 6f;
            gradeText.text = string.Empty;
            gradeText.enabled = false;

            GameObject message = Child(feedback, "Message", typeof(TextMeshProUGUI));
            RectTransform messageRect = (RectTransform)message.transform;
            messageRect.anchorMin = new Vector2(0f, 0.54f);
            messageRect.anchorMax = new Vector2(1f, 0.54f);
            messageRect.pivot = new Vector2(0.5f, 0.5f);
            messageRect.anchoredPosition = Vector2.zero;
            messageRect.sizeDelta = new Vector2(0f, 32f);
            TextMeshProUGUI messageText = message.GetComponent<TextMeshProUGUI>();
            Style(messageText, theme, 19f, TextAlignmentOptions.Center);
            messageText.text = string.Empty;
            messageText.enabled = false;

            ChopFeedbackView view = feedback.GetComponent<ChopFeedbackView>();
            Set(view, "_grade", gradeText);
            Set(view, "_message", messageText);
            Set(view, "_group", feedback.GetComponent<CanvasGroup>());
            Set(view, "_theme", theme);
        }

        private static ItemSlotView BuildSlotPrefab(UiTheme theme)
        {
            GameObject slot = new("ItemSlot");
            slot.AddComponent<RectTransform>();
            Image background = slot.AddComponent<Image>();
            background.sprite = Builtin("UI/Skin/UISprite.psd");
            background.type = Image.Type.Sliced;
            background.color = theme.Slot;
            ItemSlotView view = slot.AddComponent<ItemSlotView>();

            RectTransform rect = (RectTransform)slot.transform;
            rect.sizeDelta = new Vector2(Cell, Cell);

            /* A tier bar rather than only a tinted glyph: tier gating is a hard wall
             * (DESIGN 8.4), and it has to stay readable once real icons cover the middle
             * of the square. */
            GameObject stripe = Child(slot, "TierStripe", typeof(Image));
            RectTransform stripeRect = (RectTransform)stripe.transform;
            stripeRect.anchorMin = new Vector2(0f, 1f);
            stripeRect.anchorMax = new Vector2(1f, 1f);
            stripeRect.pivot = new Vector2(0.5f, 1f);
            stripeRect.anchoredPosition = Vector2.zero;
            stripeRect.sizeDelta = new Vector2(0f, 3f);
            Image stripeImage = stripe.GetComponent<Image>();
            stripeImage.raycastTarget = false;

            GameObject icon = Child(slot, "Icon", typeof(Image));
            Stretch(icon, 8f);
            Image iconImage = icon.GetComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
            iconImage.enabled = false;

            GameObject chip = Child(slot, "Chip", typeof(TextMeshProUGUI));
            Stretch(chip);
            TextMeshProUGUI chipText = chip.GetComponent<TextMeshProUGUI>();
            Style(chipText, theme, 22f, TextAlignmentOptions.Center);
            chipText.fontStyle = FontStyles.Bold;
            chipText.text = string.Empty;

            GameObject count = Child(slot, "Count", typeof(TextMeshProUGUI));
            Stretch(count);
            TextMeshProUGUI countText = count.GetComponent<TextMeshProUGUI>();
            Style(countText, theme, 14f, TextAlignmentOptions.BottomRight);
            countText.margin = new Vector4(0f, 0f, 5f, 3f);
            countText.text = string.Empty;

            Set(view, "_background", background);
            Set(view, "_icon", iconImage);
            Set(view, "_tierStripe", stripeImage);
            Set(view, "_chip", chipText);
            Set(view, "_count", countText);
            Set(view, "_theme", theme);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(slot, SlotPath);
            Object.DestroyImmediate(slot);
            return saved.GetComponent<ItemSlotView>();
        }

        private static void BuildInventory(GameObject root, UiTheme theme, ItemSlotView slotPrefab)
        {
            /* The screen sits on an always-active object and toggles a child, because a
             * component on a deactivated object does not run Update and could never see
             * the key that opens it. */
            GameObject screen = Child(root, "Inventory", typeof(InventoryScreen));
            Stretch(screen);

            GameObject panel = Child(screen, "Panel", typeof(Image));
            Stretch(panel);
            Image dim = panel.GetComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.55f);

            GameObject row = Child(panel, "Row", typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            Centre(row, new Vector2(0f, 0f));
            HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 16f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = false;
            ContentSizeFitter fitter = row.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject window = Window(row, theme, "Window", 6f * Cell + 5f * Gap + 48f);

            Header(window, theme, "EQUIPPED", -18f);
            GameObject equipment = Grid(window, "Equipment", 3, -52f, 2);

            Header(window, theme, "CARRIED", -212f);
            GameObject carried = Grid(window, "Carried", 6, -246f, 2);

            GameObject storage = BuildStorageWindow(row, theme);

            GameObject message = Child(window, "Message", typeof(TextMeshProUGUI));
            RectTransform messageRect = (RectTransform)message.transform;
            messageRect.anchorMin = new Vector2(0f, 0f);
            messageRect.anchorMax = new Vector2(1f, 0f);
            messageRect.pivot = new Vector2(0.5f, 0f);
            messageRect.anchoredPosition = new Vector2(0f, 16f);
            messageRect.sizeDelta = new Vector2(0f, 28f);
            TextMeshProUGUI messageText = message.GetComponent<TextMeshProUGUI>();
            Style(messageText, theme, 17f, TextAlignmentOptions.Center);
            messageText.color = theme.SlotRefusing;
            messageText.enabled = false;

            /* The ghost is a sibling of the panel rather than a child of a slot, so it
             * draws above everything, and it must never take raycasts — if it did, the
             * drop would land on the thing being dragged instead of the slot underneath. */
            GameObject ghost = Child(screen, "DragGhost", typeof(Image));
            Centre(ghost, new Vector2(180f, 34f));
            Image ghostImage = ghost.GetComponent<Image>();
            ghostImage.sprite = Builtin("UI/Skin/UISprite.psd");
            ghostImage.type = Image.Type.Sliced;
            ghostImage.color = theme.PanelHeader;
            ghostImage.raycastTarget = false;

            GameObject ghostLabel = Child(ghost, "Label", typeof(TextMeshProUGUI));
            Stretch(ghostLabel);
            TextMeshProUGUI ghostText = ghostLabel.GetComponent<TextMeshProUGUI>();
            Style(ghostText, theme, 16f, TextAlignmentOptions.Center);

            ghost.SetActive(false);
            panel.SetActive(false);

            InventoryScreen view = screen.GetComponent<InventoryScreen>();
            Set(view, "_panel", panel);
            Set(view, "_storageWindow", storage);
            Set(view, "_storageGrid", (RectTransform)storage.transform.Find("Storage"));
            Set(view, "_carriedGrid", (RectTransform)carried.transform);
            Set(view, "_equipmentGrid", (RectTransform)equipment.transform);
            Set(view, "_slotPrefab", slotPrefab);
            Set(view, "_dragGhost", (RectTransform)ghost.transform);
            Set(view, "_dragGhostLabel", ghostText);
            Set(view, "_message", messageText);
            Set(view, "_theme", theme);
        }

        /// <summary>A framed column in the row. Sized by a LayoutElement, since the
        /// row does not control its children.</summary>
        private static GameObject Window(GameObject row, UiTheme theme, string name, float width)
        {
            GameObject window = Child(row, name, typeof(Image), typeof(LayoutElement));
            RectTransform rect = (RectTransform)window.transform;
            rect.sizeDelta = new Vector2(width, WindowHeight);

            LayoutElement element = window.GetComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = WindowHeight;

            Image frame = window.GetComponent<Image>();
            frame.sprite = Builtin("UI/Skin/UISprite.psd");
            frame.type = Image.Type.Sliced;
            frame.color = theme.Panel;
            return window;
        }

        private static GameObject BuildStorageWindow(GameObject row, UiTheme theme)
        {
            GameObject window = Window(row, theme, "ChestWindow", 8f * Cell + 7f * Gap + 48f);
            Header(window, theme, "CABIN CHEST", -18f);
            Grid(window, "Storage", 8, -52f, 5);
            window.SetActive(false);
            return window;
        }

        private static TextMeshProUGUI Header(GameObject window, UiTheme theme, string text, float y)
        {
            GameObject header = Child(window, text, typeof(TextMeshProUGUI));
            RectTransform rect = (RectTransform)header.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(-48f, 26f);

            TextMeshProUGUI label = header.GetComponent<TextMeshProUGUI>();
            Style(label, theme, 15f, TextAlignmentOptions.Left);
            label.color = theme.TextDim;
            label.characterSpacing = 8f;
            label.text = text;
            return label;
        }

        private static GameObject Grid(GameObject window, string name, int columns, float y, int rows)
        {
            GameObject grid = Child(window, name, typeof(GridLayoutGroup));
            RectTransform rect = (RectTransform)grid.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(columns * Cell + (columns - 1) * Gap, rows * Cell + (rows - 1) * Gap);

            GridLayoutGroup layout = grid.GetComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(Cell, Cell);
            layout.spacing = new Vector2(Gap, Gap);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = columns;
            layout.childAlignment = TextAnchor.UpperCenter;
            return grid;
        }

        private static UiTheme EnsureTheme()
        {
            UiTheme theme = AssetDatabase.LoadAssetAtPath<UiTheme>(ThemePath);

            if (theme != null)
                return theme;

            theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.Font = TMP_Settings.defaultFontAsset;
            AssetDatabase.CreateAsset(theme, ThemePath);
            Debug.Log($"[UI] Created {ThemePath}.");
            return theme;
        }

        // ---- small helpers -----------------------------------------------------

        private static GameObject Child(GameObject parent, string name, params System.Type[] components)
        {
            GameObject go = new(name);
            go.AddComponent<RectTransform>();

            foreach (System.Type type in components)
                if (go.GetComponent(type) == null)
                    go.AddComponent(type);

            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void Stretch(GameObject go, float inset = 0f)
        {
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void Centre(GameObject go, Vector2 size)
        {
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        private static void Style(TextMeshProUGUI text, UiTheme theme, float size, TextAlignmentOptions align)
        {
            if (theme.Font != null)
                text.font = theme.Font;

            text.fontSize = size;
            text.alignment = align;
            text.color = theme.Text;
            text.raycastTarget = false;
        }

        private static Sprite Builtin(string path)
        {
            Sprite sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>(path);

            if (sprite == null)
                Debug.LogWarning($"[UI] Built-in sprite '{path}' not found; leaving it unset.");

            return sprite;
        }

        /// <summary>
        /// Writes a private serialized field.
        ///
        /// The alternative is making every wiring field public purely so a build script
        /// can reach it, which would put them on the components' API forever to save a
        /// few lines here. SerializedObject is the editor's own supported route to
        /// exactly this, and it applies the same modification the inspector would.
        /// </summary>
        private static void Set(Component component, string field, Object value)
        {
            SerializedObject so = new(component);
            SerializedProperty property = so.FindProperty(field);

            if (property == null)
            {
                Debug.LogError($"[UI] {component.GetType().Name} has no serialized field '{field}'.");
                return;
            }

            property.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
