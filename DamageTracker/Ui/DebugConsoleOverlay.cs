using System.Collections.Generic;
using System.Text;
using DamageTracker.Debug;
using Godot;

namespace DamageTracker.Ui;

public partial class DebugConsoleOverlay : PanelContainer
{
    private RichTextLabel _history = null!;
    private LineEdit _input = null!;
    private readonly Queue<string> _lines = new();

    private const int MaxLines = 20;

    public void Build()
    {
        Position = new Vector2(24, 360);
        Size = new Vector2(500, 250);
        CustomMinimumSize = new Vector2(500, 250);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4096;
        Visible = false;

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

        _history = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            ScrollFollowing = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        vbox.AddChild(_history);

        _input = new LineEdit
        {
            PlaceholderText = "type a command and press Enter (try: power vulnerable 2 enemy, draw 1, energy 3)",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _input.TextSubmitted += OnSubmitted;
        vbox.AddChild(_input);
    }

    public void Open()
    {
        Visible = true;
        _input.GrabFocus();
        _input.CallDeferred(LineEdit.MethodName.Clear);
    }

    public void Close()
    {
        Visible = false;
        _input.ReleaseFocus();
    }

    public void Toggle()
    {
        if (Visible) Close();
        else Open();
    }

    private void OnSubmitted(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Append($">>> {Bbcode.Escape(text)}");
        var result = DevConsoleBridge.Execute(text);
        Append($"<<< {Bbcode.Escape(result)}");
        _input.Clear();
        _input.GrabFocus();
    }

    private void Append(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines) _lines.Dequeue();
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.AppendLine(l);
        _history.Text = sb.ToString();
    }
}
