using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace FastTorrentDownload.Services;

/// <summary>
/// Minimal gettext-style localization. The English text is the key, so the code reads
/// naturally and an untranslated string degrades to English instead of a missing-key marker.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Spanish = "es";

    private static IReadOnlyDictionary<string, string>? _map;
    private static readonly ConcurrentDictionary<string, byte> MissingKeys = new();

    /// <summary>Two-letter code of the active language.</summary>
    public static string Language { get; private set; } = English;

    /// <summary>Strings requested through <see cref="T"/> or <see cref="I"/> that had no translation; for review runs.</summary>
    public static IReadOnlyCollection<string> Misses => MissingKeys.Keys.ToArray();

    /// <summary>Maps the stored setting ("System", "English", "Español") to a language code.</summary>
    public static string Resolve(string? setting, CultureInfo? systemCulture = null) => setting switch
    {
        "English" => English,
        "Español" => Spanish,
        _ => (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == Spanish ? Spanish : English
    };

    public static void Use(string language)
    {
        Language = language == Spanish ? Spanish : English;
        _map = Language == Spanish ? LocSpanish.Map : null;
        MissingKeys.Clear();
    }

    /// <summary>Translates a fixed string.</summary>
    public static string T(string text)
    {
        if (_map is null) return text;
        if (_map.TryGetValue(text, out var translated)) return translated;
        MissingKeys.TryAdd(text, 0);
        return text;
    }

    /// <summary>
    /// Translates an interpolated string: the key is its composite format ("Added {0} trackers to {1}."),
    /// so a translation may reorder the placeholders.
    /// </summary>
    public static string I(FormattableString text)
    {
        if (_map is null) return text.ToString(CultureInfo.CurrentCulture);
        if (_map.TryGetValue(text.Format, out var translated))
        {
            return string.Format(CultureInfo.CurrentCulture, translated, text.GetArguments());
        }

        MissingKeys.TryAdd(text.Format, 0);
        return text.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Translates text that may or may not be ours (an exception message, an engine state name)
    /// without recording a miss when it is not.
    /// </summary>
    public static string Known(string? text) =>
        text is not null && _map is not null && _map.TryGetValue(text, out var translated) ? translated : text ?? string.Empty;

    /// <summary>
    /// Translates the literal texts of a freshly built window or template instance. Bound
    /// properties are left alone: their values come from view models, which translate themselves.
    /// </summary>
    public static void Apply(Control root)
    {
        if (_map is null)
        {
            return;
        }

        foreach (var control in root.GetLogicalDescendants().OfType<Control>().Prepend(root))
        {
            switch (control)
            {
                case Window window:
                    Translate(window, Window.TitleProperty);
                    break;
                case TextBlock textBlock:
                    Translate(textBlock, TextBlock.TextProperty);
                    break;
                case TextBox textBox:
                    Translate(textBox, TextBox.PlaceholderTextProperty);
                    break;
                case HeaderedContentControl headered:
                    Translate(headered, HeaderedContentControl.HeaderProperty);
                    Translate(headered, ContentControl.ContentProperty);
                    break;
                case ContentControl content:
                    Translate(content, ContentControl.ContentProperty);
                    break;
            }

            Translate(control, ToolTip.TipProperty);
            Translate(control, AutomationProperties.NameProperty);
        }
    }

    private static void Translate(AvaloniaObject target, AvaloniaProperty property)
    {
        if (target.GetValue(property) is string text && text.Length > 0 &&
            BindingOperations.GetBindingExpressionBase(target, property) is null &&
            _map!.TryGetValue(text, out var translated))
        {
            target.SetValue(property, translated);
        }
    }
}
