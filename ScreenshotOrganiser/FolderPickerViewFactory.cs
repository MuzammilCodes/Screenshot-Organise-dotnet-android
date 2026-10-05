using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using IOPath = System.IO.Path;

namespace ScreenshotOrganiser;

public static class FolderPickerViewFactory
{
    public const string StorageRoot = "/storage/emulated/0";

    private const string AccentColor = "#1976D2";

    private static Color ThemeAccent(Context context) => new(context.GetColor(Resource.Color.folder_icon));

    private static Color CardColor(Context context) => new(context.GetColor(Resource.Color.overlay_card));
    private static Color TitleColor(Context context) => new(context.GetColor(Resource.Color.overlay_title));
    private static Color SecondaryColor(Context context) => new(context.GetColor(Resource.Color.overlay_secondary));

    private static Android.Graphics.Drawables.Drawable? TintedIcon(Context context, int resId, Color tint)
    {
        var drawable = context.GetDrawable(resId)?.Mutate();
        drawable?.SetTint(tint);
        return drawable;
    }

    // ------------------------------------------------------------------
    // Folder browser card
    // ------------------------------------------------------------------
    public static View BuildFolderPickerCard(
        Context context,
        string title,
        string confirmText,
        string currentPath,
        Action<string> onNavigate,
        Action<string> onConfirm,
        Action<string> onNewFolder,
        Action onCancel)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var card = CreateCard(context, Dp);
        card.AddView(CreateFolderTitle(context, title));

        var pathLabel = new TextView(context)
        {
            Text = currentPath.Replace(StorageRoot, "Internal storage")
        };
        pathLabel.SetTextColor(SecondaryColor(context));
        pathLabel.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        pathLabel.SetPadding(0, Dp(4), 0, Dp(10));
        pathLabel.SetSingleLine(true);
        pathLabel.Ellipsize = TextUtils.TruncateAt.Start;
        card.AddView(pathLabel);

        string[] subFolders;
        try
        {
            subFolders = Directory.GetDirectories(currentPath)
                .Where(d => !IOPath.GetFileName(d).StartsWith("."))
                .OrderBy(d => IOPath.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            subFolders = Array.Empty<string>();
        }

        bool canGoUp = !string.Equals(currentPath.TrimEnd('/'), StorageRoot, StringComparison.OrdinalIgnoreCase);

        var items = new List<string>();
        if (canGoUp) items.Add("..");
        items.AddRange(subFolders.Select(d => IOPath.GetFileName(d)));

        var listView = new ListView(context)
        {
            Adapter = new FolderListAdapter(context, items, canGoUp),
            Divider = null
        };
        listView.ItemClick += (s, e) =>
        {
            try
            {
                if (canGoUp && e.Position == 0)
                {
                    var parent = IOPath.GetDirectoryName(currentPath.TrimEnd('/')) ?? StorageRoot;
                    onNavigate(parent);
                }
                else
                {
                    var index = canGoUp ? e.Position - 1 : e.Position;
                    onNavigate(subFolders[index]);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error navigating folder: {ex.Message}");
            }
        };

        int screenHeight = context.Resources?.DisplayMetrics?.HeightPixels ?? 1920;
        var listParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, (int)(screenHeight * 0.35f));
        card.AddView(listView, listParams);

        var buttonRow = CreateButtonRow(context, Dp);
        var cancelBtn = CreateFlatButton(context, "Cancel", SecondaryColor(context));
        var newFolderBtn = CreateFlatButton(context, "New folder", Color.ParseColor(AccentColor));
        var confirmBtn = CreatePillButton(context, confirmText, Dp);

        cancelBtn.Click += (s, e) => onCancel();
        newFolderBtn.Click += (s, e) => onNewFolder(currentPath);
        confirmBtn.Click += (s, e) => onConfirm(currentPath);

        var btnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        buttonRow.AddView(cancelBtn, btnParams);
        buttonRow.AddView(newFolderBtn, btnParams);
        buttonRow.AddView(confirmBtn, btnParams);
        card.AddView(buttonRow);

        return card;
    }

    // ------------------------------------------------------------------
    // "Create new folder" card
    // ------------------------------------------------------------------
    public static View BuildNewFolderCard(
        Context context,
        string parentPath,
        Action<string> onCreated,
        Action onBack,
        Action<string> showMessage)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var card = CreateCard(context, Dp);
        card.AddView(CreateFolderTitle(context, "Create new folder"));

        var input = new EditText(context) { Hint = "Folder name" };
        input.SetTextColor(TitleColor(context));
        input.SetHintTextColor(SecondaryColor(context));
        card.AddView(input);

        var buttonRow = CreateButtonRow(context, Dp);
        var backBtn = CreateFlatButton(context, "Back", SecondaryColor(context));
        var createBtn = CreatePillButton(context, "Create", Dp);

        backBtn.Click += (s, e) => onBack();
        createBtn.Click += (s, e) =>
        {
            var name = input.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                showMessage("Enter a folder name");
                return;
            }

            try
            {
                var newPath = IOPath.Combine(parentPath, name);
                Directory.CreateDirectory(newPath);
                onCreated(newPath);
            }
            catch (Exception ex)
            {
                showMessage($"Failed to create folder: {ex.Message}");
            }
        };

        var btnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        buttonRow.AddView(backBtn, btnParams);
        buttonRow.AddView(createBtn, btnParams);
        card.AddView(buttonRow);

        return card;
    }

    // ------------------------------------------------------------------
    // Confirmation card (title + message + cancel/confirm)
    // ------------------------------------------------------------------
    public static View BuildConfirmationCard(
        Context context,
        string title,
        string message,
        string confirmText,
        Action onConfirm,
        Action onCancel)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var card = CreateCard(context, Dp);
        card.AddView(CreateFolderTitle(context, title));

        var messageLabel = new TextView(context) { Text = message };
        messageLabel.SetTextColor(SecondaryColor(context));
        messageLabel.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        messageLabel.SetPadding(0, Dp(6), 0, Dp(6));
        card.AddView(messageLabel);

        var buttonRow = CreateButtonRow(context, Dp);
        var cancelBtn = CreateFlatButton(context, "Cancel", SecondaryColor(context));
        var confirmBtn = CreatePillButton(context, confirmText, Dp);

        cancelBtn.Click += (s, e) => onCancel();
        confirmBtn.Click += (s, e) => onConfirm();

        var btnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        buttonRow.AddView(cancelBtn, btnParams);
        buttonRow.AddView(confirmBtn, btnParams);
        card.AddView(buttonRow);

        return card;
    }

    // ------------------------------------------------------------------
    // Snackbar ("Moved" + check icon)
    // ------------------------------------------------------------------
    public static View BuildSnackbarCard(Context context, string message)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var bar = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.ParseColor("#323232"));
        bg.SetCornerRadius(Dp(24));
        bar.Background = bg;
        bar.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
        bar.Elevation = Dp(6);
        bar.SetGravity(GravityFlags.CenterVertical);

        var label = new TextView(context) { Text = message };
        label.SetTextColor(Color.White);
        label.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        bar.AddView(label);

        var check = new ImageView(context);
        check.SetImageDrawable(context.GetDrawable(Resource.Drawable.ic_check));
        var checkParams = new LinearLayout.LayoutParams(Dp(18), Dp(18)) { MarginStart = Dp(8) };
        bar.AddView(check, checkParams);

        return bar;
    }

    // ------------------------------------------------------------------
    // "Screenshot detected" card
    // ------------------------------------------------------------------
    public static View BuildScreenshotDetectedCard(Context context, Action onSelectFolder, Action onCancel)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var card = CreateCard(context, Dp);

        var titleView = CreateTitle(context, "Screenshot detected!");
        titleView.Gravity = GravityFlags.CenterHorizontal;
        card.AddView(titleView);

        var messageLabel = new TextView(context) { Text = "Where would you like to save it?" };
        messageLabel.SetTextColor(SecondaryColor(context));
        messageLabel.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        messageLabel.SetPadding(0, Dp(6), 0, Dp(6));
        messageLabel.Gravity = GravityFlags.CenterHorizontal;
        card.AddView(messageLabel);

        var buttonRow = CreateButtonRow(context, Dp);
        var cancelBtn = CreateFlatButton(context, "Cancel", SecondaryColor(context));
        var selectBtn = CreatePillButton(context, "Select folder", Dp, withFolderIcon: true);

        cancelBtn.Click += (s, e) => onCancel();
        selectBtn.Click += (s, e) => onSelectFolder();

        var btnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        buttonRow.AddView(cancelBtn, btnParams);
        buttonRow.AddView(selectBtn, btnParams);
        card.AddView(buttonRow);

        return card;
    }

    public static int GetPreferredWidth(Context context)
    {
        int screenWidth = context.Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        var screenWidthDp = screenWidth / density;

        return screenWidthDp >= 600
            ? (int)(screenWidth * 0.5f)
            : (int)(screenWidth * 0.88f);
    }

    // ------------------------------------------------------------------
    // List adapter: icon + name per row; first row is "Back" when not at root
    // ------------------------------------------------------------------
    private sealed class FolderListAdapter : ArrayAdapter<string>
    {
        private readonly bool _canGoUp;

        public FolderListAdapter(Context context, IList<string> items, bool canGoUp)
            : base(context, Android.Resource.Layout.SimpleListItem1, items)
        {
            _canGoUp = canGoUp;
        }

        public override View GetView(int position, View? convertView, ViewGroup parent)
        {
            var density = Context.Resources?.DisplayMetrics?.Density ?? 1f;
            int Dp(int dp) => (int)(dp * density);

            LinearLayout row;
            ImageView icon;
            TextView label;

            if (convertView is LinearLayout existing && existing.ChildCount == 2)
            {
                row = existing;
                icon = (ImageView)existing.GetChildAt(0)!;
                label = (TextView)existing.GetChildAt(1)!;
            }
            else
            {
                row = new LinearLayout(Context) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(8), Dp(10), Dp(8), Dp(10));

                icon = new ImageView(Context);
                row.AddView(icon, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

                label = new TextView(Context);
                label.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
                row.AddView(label, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                { MarginStart = Dp(12) });
            }

            label.SetTextColor(TitleColor(Context));

            bool isUpRow = _canGoUp && position == 0;
            icon.SetImageDrawable(TintedIcon(
                Context, isUpRow ? Resource.Drawable.ic_back : Resource.Drawable.ic_folder, ThemeAccent(Context)));
            label.Text = isUpRow ? "Back" : GetItem(position);

            return row;
        }
    }

    // ------------------------------------------------------------------
    // Shared building blocks
    // ------------------------------------------------------------------
    private static LinearLayout CreateCard(Context context, Func<int, int> dp)
    {
        var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var cardBg = new Android.Graphics.Drawables.GradientDrawable();
        cardBg.SetColor(CardColor(context));
        cardBg.SetCornerRadius(dp(20));
        card.Background = cardBg;
        card.SetPadding(dp(20), dp(20), dp(20), dp(12));
        card.Elevation = dp(8);
        return card;
    }

    private static TextView CreateTitle(Context context, string text)
    {
        var title = new TextView(context) { Text = text };
        title.SetTextColor(TitleColor(context));
        title.SetTextSize(Android.Util.ComplexUnitType.Sp, 18);
        title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return title;
    }

    private static LinearLayout CreateFolderTitle(Context context, string text)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(int dp) => (int)(dp * density);

        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);

        var icon = new ImageView(context);
        icon.SetImageDrawable(TintedIcon(context, Resource.Drawable.ic_folder, ThemeAccent(context)));
        row.AddView(icon, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

        row.AddView(CreateTitle(context, text), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { MarginStart = Dp(10) });

        return row;
    }

    private static LinearLayout CreateButtonRow(Context context, Func<int, int> dp)
    {
        var buttonRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        buttonRow.SetPadding(0, dp(10), 0, 0);
        return buttonRow;
    }

    private static Button CreateFlatButton(Context context, string text, Color textColor)
    {
        var btn = new Button(context) { Text = text };
        btn.SetTextColor(textColor);
        btn.SetBackgroundColor(Color.Transparent);
        btn.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        btn.SetAllCaps(false);
        return btn;
    }

    private static Button CreatePillButton(Context context, string text, Func<int, int> dp, bool withFolderIcon = false)
    {
        var btn = new Button(context) { Text = text };
        btn.SetTextColor(Color.White);
        btn.SetAllCaps(false);
        btn.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);

        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.ParseColor(AccentColor));
        bg.SetCornerRadius(dp(20));
        btn.Background = bg;
        btn.SetPadding(dp(16), 0, dp(16), 0);

        if (withFolderIcon)
        {
            // Inline ImageSpan keeps the icon tight against the centered text
            var icon = TintedIcon(context, Resource.Drawable.ic_folder, Color.White)!;
            icon.SetBounds(0, 0, dp(15), dp(15));

            var alignment = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? SpanAlign.Center
                : SpanAlign.Baseline;

            var spannable = new SpannableString("x " + text);
            spannable.SetSpan(new ImageSpan(icon, alignment), 0, 1, SpanTypes.ExclusiveExclusive);
            btn.TextFormatted = spannable;
        }

        return btn;
    }
}
