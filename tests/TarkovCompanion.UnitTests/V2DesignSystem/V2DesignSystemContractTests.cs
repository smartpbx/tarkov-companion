using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TarkovCompanion.App.Themes.V2;
using TarkovCompanion.App.Views.V2.Primitives;
using static TarkovCompanion.UnitTests.V2DesignSystem.V2DesignSystemFiles;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

/// <summary>
/// Structural checks on the V2 manifest, resources, styles, and gallery source. None of these
/// render anything: they prove what the sources declare, not what a screen reader hears or what a
/// 200% layout looks like. That evidence belongs to #279.
/// </summary>
public sealed class V2DesignSystemContractTests
{
    private static readonly string[] Panels = ["StackPanel", "WrapPanel", "Grid", "Panel", "Border", "DockPanel"];

    [Fact]
    public void ManifestTokensNameResourcesThatExistAndEveryResourceHasAToken()
    {
        using var manifest = ReadJson(ManifestPath);
        var resourceKeys = AdapterResourceKeys();
        var tokenNames = new List<string>();

        foreach (var group in manifest.RootElement.GetProperty("tokens").EnumerateObject())
        {
            var mapped = TokenPrefixes.TryGetValue(group.Name, out var prefix);
            Assert.True(mapped, $"Manifest token group '{group.Name}' has no Avalonia prefix.");

            foreach (var token in group.Value.EnumerateArray())
            {
                var name = prefix + Pascal(token.GetString()!);
                tokenNames.Add(name);
                var realised = resourceKeys.Where(key => Names(key, name)).ToArray();
                Assert.NotEmpty(realised);
            }
        }

        var orphans = resourceKeys.Where(key => !tokenNames.Exists(name => Names(key, name))).ToArray();
        Assert.Empty(orphans);
    }

    [Fact]
    public void BaseThemesDefineTheSameRolesAndColourVisionThemesReplaceOnlyStatusRoles()
    {
        var themes = ThemeDictionaries();
        string[] colourVision = ["DarkRedGreenSafe", "LightRedGreenSafe", "DarkBlueYellowSafe", "LightBlueYellowSafe", "DarkMonochrome", "LightMonochrome"];
        string[] statusKeys = ["V2.Brush.Danger", "V2.Brush.Info", "V2.Brush.Success", "V2.Brush.Unknown", "V2.Brush.Warning"];
        string[] expectedThemes = ["Dark", "Light", "HighContrast", .. colourVision];

        Assert.Equal(expectedThemes.Order(StringComparer.Ordinal), themes.Keys.Order(StringComparer.Ordinal));
        var dark = KeysOf(themes["Dark"]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(dark, KeysOf(themes["Light"]).Order(StringComparer.Ordinal));
        Assert.Equal(dark, KeysOf(themes["HighContrast"]).Order(StringComparer.Ordinal));

        foreach (var name in colourVision)
        {
            Assert.Equal(statusKeys, KeysOf(themes[name]).Order(StringComparer.Ordinal));
        }

        // Every custom dictionary key must be an x:Static reference. A bare string such as
        // "HighContrast" compiles, then throws in ThemeVariantTypeConverter when the dictionary loads.
        foreach (var (name, dictionary) in themes)
        {
            if (name is not ("Dark" or "Light"))
            {
                Assert.StartsWith("{x:Static v2:V2Appearance.", Key(dictionary), StringComparison.Ordinal);
                Assert.NotNull(typeof(V2Appearance).GetProperty(name));
            }
        }
    }

    [Fact]
    public void StatusAxesStayIndependentAndEveryValueHasAWordAGlyphAndResources()
    {
        using var manifest = ReadJson(ManifestPath);
        var status = manifest.RootElement.GetProperty("status");
        var axes = status.GetProperty("axes");
        var english = ReadStrings(EnglishStringsPath);
        var glyphKeys = KeysOf(ReadXaml(TokensPath));
        string[] expectedAxes = ["availability", "completeness", "freshness"];

        Assert.Equal(expectedAxes, axes.EnumerateObject().Select(axis => axis.Name));
        Assert.Equal(expectedAxes, Strings(status.GetProperty("composition"), "order"));

        var glyphs = new List<string>();
        var wording = new List<string>();
        foreach (var axis in axes.EnumerateObject())
        {
            var values = axis.Value.GetProperty("values").EnumerateArray().ToArray();
            var ids = values.Select(value => value.GetProperty("id").GetString()!).ToArray();
            Assert.Contains("unknown", ids);

            var defaultValue = axis.Value.GetProperty("default");
            if (defaultValue.ValueKind != JsonValueKind.Null)
            {
                Assert.Contains(defaultValue.GetString()!, ids);
            }

            foreach (var value in values)
            {
                var wordingKey = value.GetProperty("wordingKey").GetString()!;
                var glyphKey = "V2.Glyph." + Pascal(value.GetProperty("glyph").GetString()!);
                var hasWord = english.ContainsKey(wordingKey);
                var hasGlyph = glyphKeys.Contains(glyphKey);
                var hasBorder = value.TryGetProperty("border", out _);

                Assert.True(hasWord, $"{axis.Name} wording {wordingKey} is not a string resource.");
                Assert.True(hasGlyph, $"{axis.Name} glyph {glyphKey} is not a token.");
                if (axis.Name == "availability")
                {
                    Assert.True(hasBorder, $"Availability value {wordingKey} has no border pattern.");
                }
                else
                {
                    Assert.False(hasBorder, $"{axis.Name} value {wordingKey} competes with availability for the outline.");
                }

                glyphs.Add(glyphKey);
                wording.Add(wordingKey);
            }
        }

        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(wording.Count, wording.Distinct(StringComparer.Ordinal).Count());

        // The first manifest listed stale and partial beside offline and failed, as though a result
        // could not be partial and stale at once.
        var availability = axes.GetProperty("availability").GetProperty("values").EnumerateArray()
            .Select(value => value.GetProperty("id").GetString()!).ToArray();
        Assert.DoesNotContain("stale", availability);
        Assert.DoesNotContain("partial", availability);
    }

    [Fact]
    public void StringResourcesAgreeAndThePseudoLocaleIsGeneratedFromEnglish()
    {
        var english = ReadStrings(EnglishStringsPath);
        var pseudo = ReadStrings(PseudoStringsPath);
        var axaml = ReadXaml(StringsXamlPath).Elements(Xaml + "String")
            .ToDictionary(element => Key(element)!, element => element.Value, StringComparer.Ordinal);

        Assert.Equal(Pairs(english), Pairs(axaml));
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), pseudo.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in english)
        {
            Assert.Equal(V2PresentationFormatting.PseudoLocalize(value), pseudo[key]);
        }

        Assert.Equal("[Ëxåmplë ···]", V2PresentationFormatting.PseudoLocalize("Example"));
        Assert.Equal("[{0} ïtëms ····]", V2PresentationFormatting.PseudoLocalize("{0} items"));

        using var manifest = ReadJson(ManifestPath);
        var referenced = DynamicResourceKeys(ReadText(GalleryPath))
            .Where(key => key.StartsWith("V2.String.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        referenced.UnionWith(WordingKeys(manifest.RootElement));

        Assert.Empty(referenced.Except(english.Keys, StringComparer.Ordinal));
        Assert.Empty(english.Keys.Except(referenced, StringComparer.Ordinal));
    }

    [Fact]
    public void GalleryAndStylesReferenceOnlyDefinedResources()
    {
        var defined = AdapterResourceKeys();
        defined.UnionWith(KeysOf(ReadXaml(TokensPath)));
        defined.UnionWith(ReadStrings(EnglishStringsPath).Keys);

        var undefined = DynamicResourceKeys(ReadText(GalleryPath))
            .Concat(DynamicResourceKeys(ReadText(StylesPath)))
            .Where(key => !defined.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(undefined);
    }

    [Fact]
    public void GalleryHeadingsLandmarksNamesAndLiveRegionsUseMappedAutomationProperties()
    {
        var elements = ReadXaml(GalleryPath).Descendants().ToArray();

        var headings = elements.Where(element => Attr(element, "AutomationProperties.HeadingLevel") is not null).ToArray();
        Assert.Single(headings, heading => Attr(heading, "AutomationProperties.HeadingLevel") == "1");
        var previous = 0;
        foreach (var heading in headings)
        {
            var level = int.Parse(Attr(heading, "AutomationProperties.HeadingLevel")!, CultureInfo.InvariantCulture);
            Assert.True(level <= previous + 1, $"Heading level {level} follows level {previous}.");
            Assert.Contains($"v2-heading-{level}", Classes(heading));
            previous = level;
        }

        Assert.All(
            elements.Where(element => Array.Exists(Classes(element), name => name.StartsWith("v2-heading-", StringComparison.Ordinal))),
            element => Assert.NotNull(Attr(element, "AutomationProperties.HeadingLevel")));

        // A panel's NoneAutomationPeer is outside the UIA control view unless told otherwise, so a
        // landmark, name, or control type on one is invisible to Narrator without this.
        foreach (var panel in elements.Where(element => Panels.Contains(element.Name.LocalName)))
        {
            var exposes = Attr(panel, "AutomationProperties.LandmarkType") ?? Attr(panel, "AutomationProperties.ControlTypeOverride") ?? Attr(panel, "AutomationProperties.Name");
            if (exposes is not null)
            {
                Assert.Equal("Control", Attr(panel, "AutomationProperties.AccessibilityView"));
            }
        }

        string[] landmarks = ["Main"];
        Assert.Equal(landmarks, elements.Select(element => Attr(element, "AutomationProperties.LandmarkType")).OfType<string>());
        var toolbar = ById(elements, "v2-toolbar");
        Assert.Equal("ToolBar", Attr(toolbar, "AutomationProperties.ControlTypeOverride"));
        Assert.Null(Attr(toolbar, "AutomationProperties.LandmarkType"));

        Assert.All(ByType(elements, "TextBox"), box => Assert.NotNull(Attr(box, "AutomationProperties.Name") ?? Attr(box, "AutomationProperties.LabeledBy")));
        Assert.All(ByType(elements, "Button"), button => Assert.NotNull(Attr(button, "Content") ?? Attr(button, "AutomationProperties.Name")));
        Assert.All(ByType(elements, "Expander"), expander => Assert.NotNull(Attr(expander, "AutomationProperties.Name")));

        // LiveSetting does not inherit, and only a name change on the element itself raises
        // LiveRegionChanged; for a TextBlock that is a Text change.
        var live = elements.Where(element => Attr(element, "AutomationProperties.LiveSetting") is not null).ToArray();
        Assert.All(live, element =>
        {
            Assert.Equal("TextBlock", element.Name.LocalName);
            Assert.NotNull(Attr(element, "AutomationProperties.AutomationId"));
        });
        string[] liveSettings = ["Assertive", "Polite"];
        Assert.Equal(liveSettings, live.Select(element => Attr(element, "AutomationProperties.LiveSetting")!).Order(StringComparer.Ordinal));

        var decorative = elements.Where(element => element.Name.LocalName == "Rectangle" || Classes(element).Contains("v2-glyph"));
        Assert.All(decorative, element => Assert.Equal("Raw", Attr(element, "AutomationProperties.AccessibilityView")));
    }

    [Fact]
    public void GalleryTableAvoidsIneffectiveHeaderPropertiesAndNamesEveryRow()
    {
        var gallery = ReadXaml(GalleryPath);
        var elements = gallery.Descendants().ToArray();
        var styleSetters = ReadXaml(StylesPath).Descendants(AvaloniaXmlns + "Setter").Select(setter => Attr(setter, "Property")).OfType<string>();

        // Avalonia 12.1.2 documents both as "currently has no effect" and implements no UIA table or
        // grid pattern; a table built on them reads as loose text.
        string[] ineffective = ["AutomationProperties.IsColumnHeader", "AutomationProperties.IsRowHeader"];
        var used = elements.SelectMany(element => element.Attributes()).Select(attribute => attribute.Name.LocalName)
            .Concat(styleSetters)
            .Intersect(ineffective, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(used);

        var table = ById(elements, "v2-semantic-table");
        Assert.Equal("Control", Attr(table, "AutomationProperties.AccessibilityView"));
        Assert.NotNull(Attr(table, "AutomationProperties.Name"));

        var rows = table.Elements().Where(element => Classes(element).Contains("v2-table-row")).ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal("Control", Attr(row, "AutomationProperties.AccessibilityView"));
            Assert.EndsWith(".Sentence}", Attr(row, "AutomationProperties.Name"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void GalleryCopyIsResourceBackedAndCarriesNoLiteralColourOrType()
    {
        var elements = ReadXaml(GalleryPath).Descendants().ToArray();
        string[] copyAttributes = ["Text", "Content", "Header", "ToolTip.Tip", "AutomationProperties.Name", "AutomationProperties.HelpText"];

        var copy = elements.SelectMany(element => element.Attributes()).Where(attribute => copyAttributes.Contains(attribute.Name.LocalName)).ToArray();
        Assert.NotEmpty(copy);
        Assert.All(copy, attribute => Assert.StartsWith("{DynamicResource V2.", attribute.Value, StringComparison.Ordinal));

        string[] styledProperties = ["FontSize", "Foreground", "Background", "BorderBrush", "Fill", "Stroke", "Padding", "Spacing", "MinHeight", "MinWidth"];
        var literal = elements.SelectMany(element => element.Attributes())
            .Where(attribute => styledProperties.Contains(attribute.Name.LocalName) && !attribute.Value.StartsWith("{DynamicResource V2.", StringComparison.Ordinal))
            .Select(attribute => $"{attribute.Parent!.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}")
            .ToArray();
        Assert.Empty(literal);
    }

    [Fact]
    public void GalleryBadgesAndLegendsCarryTheManifestWordGlyphAndPattern()
    {
        using var manifest = ReadJson(ManifestPath);
        var elements = ReadXaml(GalleryPath).Descendants().ToArray();
        var availability = manifest.RootElement.GetProperty("status").GetProperty("axes").GetProperty("availability").GetProperty("values")
            .EnumerateArray()
            .ToDictionary(
                value => value.GetProperty("id").GetString()!,
                value => (Word: value.GetProperty("wordingKey").GetString()!, Glyph: value.GetProperty("glyph").GetString()!, Pattern: value.GetProperty("border").GetString()!),
                StringComparer.Ordinal);

        var badges = elements.Where(element => Classes(element).Contains("v2-badge")).ToArray();
        Assert.NotEmpty(badges);
        foreach (var badge in badges)
        {
            var id = Classes(badge).Single(name => name.StartsWith("v2-availability-", StringComparison.Ordinal))["v2-availability-".Length..];
            var expected = availability[id];
            AssertPattern(expected.Pattern, badge.Elements(AvaloniaXmlns + "Rectangle").Single());

            var texts = badge.Descendants(AvaloniaXmlns + "TextBlock").Select(text => Attr(text, "Text")).OfType<string>().ToArray();
            Assert.Contains($"{{DynamicResource V2.Glyph.{Pascal(expected.Glyph)}}}", texts);
            Assert.Contains($"{{DynamicResource {expected.Word}}}", texts);
        }

        foreach (var (legendId, classPrefix, entriesProperty) in new[] { ("v2-map-legend", "v2-map-", "mapEvidence"), ("v2-chart-legend", "v2-chart-", "chartSeries") })
        {
            var swatches = ById(elements, legendId).Descendants(AvaloniaXmlns + "Rectangle").ToArray();
            var entries = manifest.RootElement.GetProperty(entriesProperty).EnumerateArray().ToArray();
            Assert.Equal(entries.Length, swatches.Length);

            foreach (var entry in entries)
            {
                var swatch = Assert.Single(swatches, candidate => Classes(candidate).Contains(classPrefix + entry.GetProperty("id").GetString()));
                AssertPattern(entry.GetProperty("pattern").GetString()!, swatch);
                if (entry.TryGetProperty("wordingKey", out var wordingKey))
                {
                    var word = swatch.Parent!.Elements(AvaloniaXmlns + "TextBlock").Single();
                    Assert.Equal($"{{DynamicResource {wordingKey.GetString()}}}", Attr(word, "Text"));
                }
            }
        }
    }

    [Fact]
    public void EveryPrimitiveIsEitherDemonstratedOrDeferredToAnOwner()
    {
        using var manifest = ReadJson(ManifestPath);
        var automationIds = ReadXaml(GalleryPath).Descendants()
            .Select(element => Attr(element, "AutomationProperties.AutomationId"))
            .OfType<string>()
            .ToArray();

        Assert.Equal(Strings(manifest.RootElement, "primitives").Order(StringComparer.Ordinal), V2PrimitiveContracts.Required.Order(StringComparer.Ordinal));
        var covered = V2PrimitiveContracts.GalleryExamples.Keys.Concat(V2PrimitiveContracts.Deferred.Keys);
        Assert.Equal(V2PrimitiveContracts.Required.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
        Assert.All(V2PrimitiveContracts.GalleryExamples.Values, automationId => Assert.Contains(automationId, automationIds));
        Assert.All(V2PrimitiveContracts.Deferred.Values, owner => Assert.StartsWith("#", owner, StringComparison.Ordinal));

        var operations = manifest.RootElement.GetProperty("operations");
        Assert.Equal(Strings(operations, "captureStages"), V2PrimitiveContracts.CaptureStages);
        Assert.Equal(Strings(operations, "pairedDeviceStates"), V2PrimitiveContracts.DeviceStates);
    }

    [Fact]
    public void TypeStylesUseTheirLevelTokensAndDensityOnlyChangesWhitespace()
    {
        var tokens = ReadXaml(TokensPath).Elements()
            .Where(element => Key(element) is not null)
            .ToDictionary(element => Key(element)!, element => element.Value.Trim(), StringComparer.Ordinal);
        var floors = new Dictionary<string, (string Size, string LineHeight)>(StringComparer.Ordinal)
        {
            ["Heading1"] = ("28", "36"),
            ["Heading2"] = ("22", "28"),
            ["Heading3"] = ("18", "24"),
            ["Body"] = ("16", "24"),
            ["Label"] = ("14", "20"),
        };
        foreach (var (role, (size, lineHeight)) in floors)
        {
            Assert.Equal(size, tokens[$"V2.Type.{role}.Size"]);
            Assert.Equal(lineHeight, tokens[$"V2.Type.{role}.LineHeight"]);
        }

        var sizes = tokens.Where(pair => pair.Key.StartsWith("V2.Type.", StringComparison.Ordinal) && pair.Key.EndsWith(".Size", StringComparison.Ordinal)).ToArray();
        Assert.All(sizes, pair => Assert.InRange(double.Parse(pair.Value, CultureInfo.InvariantCulture), 14d, 72d));
        Assert.Equal("44", tokens["V2.Target.Desktop"]);
        Assert.Equal("48", tokens["V2.Target.Touch"]);

        var styles = ReadXaml(StylesPath).Elements(AvaloniaXmlns + "Style").ToArray();

        // The section heading once used the 18 DIP subheading size under HeadingLevel 2.
        foreach (var level in new[] { 1, 2, 3 })
        {
            var setters = Setters(styles, $"TextBlock.v2-heading-{level}");
            Assert.Equal($"{{DynamicResource V2.Type.Heading{level}.Size}}", setters["FontSize"]);
            Assert.Equal($"{{DynamicResource V2.Type.Heading{level}.LineHeight}}", setters["LineHeight"]);
        }

        string[] whitespace = ["Padding", "Spacing"];
        foreach (var style in styles.Where(style => Attr(style, "Selector")!.Contains("v2-density-", StringComparison.Ordinal)))
        {
            Assert.All(style.Elements(AvaloniaXmlns + "Setter"), setter => Assert.Contains(Attr(setter, "Property")!, whitespace));
        }

        var densityKeys = tokens.Keys.Where(key => key.StartsWith("V2.Density.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(densityKeys);
        Assert.All(densityKeys, key => Assert.Matches(@"^V2\.Density\.[A-Za-z]+\.(Gap|Inset)$", key));

        // Elevation adds a shadow; it never stands in for the boundary.
        foreach (var style in styles.Where(style => Setters(style).ContainsKey("BoxShadow")))
        {
            var setters = Setters(style);
            var properties = setters.Keys.ToArray();
            Assert.Contains("BorderBrush", properties);
            Assert.Contains("BorderThickness", properties);
        }

        foreach (var pattern in new[] { "v2-pattern-dashed", "v2-pattern-dotted", "v2-pattern-dash-dot" })
        {
            Assert.Contains(styles, style => Attr(style, "Selector") == $"Rectangle.{pattern}");
        }
    }

    [Fact]
    public void RenderMatrixIsAnUnrenderedChecklistCoveringEveryScaleWidthAndVariant()
    {
        using var matrix = ReadJson(RenderMatrixPath);
        using var manifest = ReadJson(ManifestPath);
        var root = matrix.RootElement;
        var adaptation = manifest.RootElement.GetProperty("adaptation");
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();

        // The first version was named a visual baseline and described as checked at every scale,
        // while nothing rendered it. It stays a checklist until #279 attaches real captures.
        Assert.Equal("unrendered", root.GetProperty("status").GetString());
        Assert.Equal("#279", root.GetProperty("evidenceOwner").GetString());

        var scales = cases.Select(item => item.GetProperty("textScale").GetInt32().ToString(CultureInfo.InvariantCulture)).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(Strings(adaptation, "textScales").Order(StringComparer.Ordinal), scales);

        var widths = Strings(adaptation, "effectiveContentWidths").ToArray();
        Assert.All(cases, item => Assert.Contains(item.GetProperty("effectiveWidth").GetString()!, widths));
        Assert.Contains(cases, item => item.GetProperty("effectiveWidth").GetString() == "narrow");
        Assert.Contains(cases, item => Strings(item, "mustShow").Contains("focusRing"));
        Assert.Contains(cases, item => Strings(item, "mustShow").Contains("orderedDataAlternative"));

        var variants = Strings(root, "variants").ToArray();
        var colourVision = Strings(manifest.RootElement.GetProperty("variants"), "colorVision").Where(name => name != "standard");
        Assert.All(new[] { "dark", "light", "highContrast" }.Concat(colourVision), name => Assert.Contains(name, variants));
    }

    [Fact]
    public void AppearancePreferencesMatchTheManifestVariants()
    {
        using var manifest = ReadJson(ManifestPath);
        var variants = manifest.RootElement.GetProperty("variants");

        Assert.Equal(Strings(variants, "appearance"), Enum.GetNames<V2AppearancePreference>().Select(Camel));
        Assert.Equal(Strings(variants, "colorVision"), Enum.GetNames<V2ColorVisionPreference>().Select(Camel));
    }

    [Fact]
    public void FormattingUsesTheSuppliedCultureAndTimeZoneRatherThanTheHost()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("V2 test +02:00", TimeSpan.FromHours(2), "V2 test", "V2 test");
        var utc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Tuesday, 01 September 2026 14:00", V2PresentationFormatting.DateTime(utc, CultureInfo.InvariantCulture, zone));
        Assert.Equal("1,234.50", V2PresentationFormatting.Number(1234.5m, CultureInfo.InvariantCulture));
        Assert.Equal("¤1,234.50", V2PresentationFormatting.Currency(1234.5m, CultureInfo.InvariantCulture));
    }

    private static bool Names(string key, string name) =>
        key == name || key.StartsWith(name + ".", StringComparison.Ordinal);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<string> Pairs(Dictionary<string, string> strings) =>
        strings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}");

    private static IEnumerable<string> WordingKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "wordingKey")
                {
                    yield return property.Value.GetString()!;
                    continue;
                }

                foreach (var key in WordingKeys(property.Value))
                {
                    yield return key;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var key in WordingKeys(item))
                {
                    yield return key;
                }
            }
        }
    }

    private static XElement ById(IEnumerable<XElement> elements, string automationId) =>
        elements.Single(element => Attr(element, "AutomationProperties.AutomationId") == automationId);

    private static XElement[] ByType(IEnumerable<XElement> elements, string type) =>
        elements.Where(element => element.Name.LocalName == type).ToArray();

    private static Dictionary<string, string> Setters(XElement style) =>
        style.Elements(AvaloniaXmlns + "Setter").ToDictionary(setter => Attr(setter, "Property")!, setter => Attr(setter, "Value")!, StringComparer.Ordinal);

    private static Dictionary<string, string> Setters(IEnumerable<XElement> styles, string selector) =>
        Setters(styles.Single(style => Attr(style, "Selector") == selector));

    private static void AssertPattern(string pattern, XElement shape)
    {
        var patternClasses = Classes(shape).Where(name => name.StartsWith("v2-pattern-", StringComparison.Ordinal)).ToArray();
        var expected = pattern switch
        {
            "solid" => null,
            "dashed" => "v2-pattern-dashed",
            "dotted" => "v2-pattern-dotted",
            "dashDot" => "v2-pattern-dash-dot",
            _ => throw new InvalidOperationException($"The manifest names an unknown pattern '{pattern}'."),
        };

        if (expected is null)
        {
            Assert.Empty(patternClasses);
        }
        else
        {
            Assert.Equal(expected, Assert.Single(patternClasses));
        }
    }
}
