using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DamageTracker.Tracking;
using Godot;

namespace DamageTracker.Ui;

/// <summary>
/// Floating Godot Control with a scope dropdown (Combat/Act/Run) and a ranked
/// list of sources by damage then block descending. Toggled with F10.
/// </summary>
public partial class DamageTableOverlay : PanelContainer
{
    private OptionButton _scopeSelector = null!;
    private RichTextLabel _body = null!;

    private const int RefreshMs = 250;
    private const Key ToggleKey = Key.F10;

    // Setup is invoked explicitly by OverlayController after AddChild rather
    // than via _Ready(). Godot's CSharpInstanceBridge only dispatches virtual
    // overrides (_Ready, _Input, _Process, _ExitTree) for scripts it knows
    // about at engine startup; types loaded from a mod DLL after init are
    // never registered with that bridge, so overrides never fire.
    public void Build()
    {
        ModEntry.Log("Overlay Build: entered");

        // CanvasLayer parents don't run a Container layout pass, so the panel's
        // rect comes from Position/Size directly. CustomMinimumSize still helps
        // when the user resizes the contents larger than the initial size.
        Position = new Vector2(24, 24);
        Size = new Vector2(360, 320);
        CustomMinimumSize = new Vector2(360, 320);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4096;

        // Force a visible background regardless of the game's active theme. The
        // game's default theme may not provide a `panel` stylebox for
        // PanelContainer, which would otherwise make the overlay invisible.
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.85f),
            BorderColor = new Color(0.4f, 0.7f, 1.0f, 0.8f),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        AddThemeStyleboxOverride("panel", bg);

        var vbox = new VBoxContainer();
        AddChild(vbox);

        var header = new HBoxContainer();
        vbox.AddChild(header);

        header.AddChild(new Label { Text = "Damage Tracker" });
        _scopeSelector = new OptionButton();
        _scopeSelector.AddItem("Combat", (int)TrackingScope.Combat);
        _scopeSelector.AddItem("Act",    (int)TrackingScope.Act);
        _scopeSelector.AddItem("Run",    (int)TrackingScope.Run);
        _scopeSelector.Selected = 0;
        _scopeSelector.ItemSelected += OnScopeSelected;
        header.AddChild(_scopeSelector);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };
        vbox.AddChild(_body);

        DamageTrackerService.Instance.Changed += QueueRefresh;
        OverlayController.ScopeChanged += QueueRefresh;
        TreeExiting += OnExiting;

        var refresh = new Godot.Timer { WaitTime = RefreshMs / 1000.0, Autostart = true, OneShot = false };
        refresh.Timeout += Refresh;
        AddChild(refresh);

        ModEntry.Log($"Overlay Build: pos={Position} size={Size} children={GetChildCount()}");
        Refresh();
    }

    private void OnExiting()
    {
        DamageTrackerService.Instance.Changed -= QueueRefresh;
        OverlayController.ScopeChanged -= QueueRefresh;
    }

    private void OnScopeSelected(long index)
    {
        var scope = (TrackingScope)_scopeSelector.GetItemId((int)index);
        OverlayController.SetScope(scope);
    }

    private void QueueRefresh() => CallDeferred(nameof(Refresh));

    private void Refresh()
    {
        var scope = OverlayController.CurrentScope;
        var rows = DamageTrackerService.Instance.Ranked(scope);
        var cardLikeNames = new HashSet<string>(
            rows.Where(r => r.Key.Kind == SourceKind.Card || r.Key.Kind == SourceKind.Relic)
                .Select(r => r.Key.DisplayName),
            StringComparer.OrdinalIgnoreCase);
        rows = rows.Where(r => r.Key.Kind != SourceKind.Power || !cardLikeNames.Contains(r.Key.DisplayName)).ToList();

        var sb = new StringBuilder();
        sb.Append("[table=3][cell][b]Source[/b][/cell][cell][b]Dmg[/b][/cell][cell][b]Blk[/b][/cell]");
        if (rows.Count == 0)
        {
            sb.Append("[cell][i]No data yet[/i][/cell][cell]-[/cell][cell]-[/cell]");
        }
        else
        {
            foreach (var (key, totals) in rows.Take(40))
            {
                sb.Append($"[cell]{key.Kind.ToString()[0]} {Bbcode.Escape(key.DisplayName)}[/cell]");
                sb.Append($"[cell]{totals.Damage}[/cell]");
                sb.Append($"[cell]{totals.Block}[/cell]");
            }
        }
        foreach (var pid in DamageTracker.Patches.LedgerPolicy.MultiplicativeEnemyDebuffs)
        {
            var v = DamageTrackerService.Instance.GetGlobalDerivedDamage(pid, scope);
            if (v <= 0) continue;
            var name = DamageTrackerService.Instance.PowerNames.Resolve(pid);
            sb.Append($"[cell]{Bbcode.Escape(name)} damage[/cell]");
            sb.Append($"[cell]{v}[/cell]");
            sb.Append("[cell]-[/cell]");
        }
        sb.Append("[/table]");

        _body.Text = sb.ToString();
    }
}

internal static class Bbcode
{
    public static string Escape(string s) =>
        s.Replace("[", "[lb]");
}
