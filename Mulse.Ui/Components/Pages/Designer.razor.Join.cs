using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// The generic module-settings editor (rendered via RenderTreeBuilder) and the multi-source
// join/map augment editor (join keys + field mappings, persisted as JSON node settings).
public partial class Designer
{
    private RenderFragment RenderSettingsEditor(IReadOnlyList<ModuleSettingViewModel>? settingDescriptors, Dictionary<string, string> settings) => builder =>
    {
        if (settingDescriptors is null || settingDescriptors.Count == 0)
        {
            return;
        }

        // Sequence numbers below are fixed integer literals representing each call site's source
        // order, per the Blazor RenderTreeBuilder guidance (see ASP0006): a given literal always
        // corresponds to the same logical position in this method, including across loop
        // iterations and mutually-exclusive switch branches (only one branch executes per
        // setting, since a setting's InputKind never changes between renders).
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "settings-grid");

        foreach (var setting in settingDescriptors)
        {
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "class", "setting-row");

            builder.OpenElement(4, "label");
            builder.AddAttribute(5, "class", "field-label");
            builder.AddContent(6, setting.Label);
            if (setting.IsRequired)
            {
                builder.AddContent(7, " *");
            }
            builder.CloseElement();

            switch (setting.InputKind)
            {
                case "TextArea":
                    builder.OpenElement(8, "textarea");
                    builder.AddAttribute(9, "class", "sample-textarea");
                    builder.AddAttribute(10, "value", GetSettingValue(settings, setting));
                    builder.AddAttribute(11, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, args => SetSettingValue(settings, setting.Key, args.Value?.ToString())));
                    builder.CloseElement();
                    break;
                case "Boolean":
                    builder.OpenElement(8, "label");
                    builder.AddAttribute(9, "class", "field-label checkbox-row");
                    builder.OpenElement(10, "input");
                    builder.AddAttribute(11, "type", "checkbox");
                    builder.AddAttribute(12, "checked", string.Equals(GetSettingValue(settings, setting), "true", StringComparison.OrdinalIgnoreCase));
                    builder.AddAttribute(13, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, args => SetBooleanSettingValue(settings, setting.Key, GetBooleanValue(args))));
                    builder.CloseElement();
                    builder.OpenElement(14, "span");
                    builder.AddContent(15, setting.Description);
                    builder.CloseElement();
                    builder.CloseElement();
                    break;
                case "Select":
                    builder.OpenElement(8, "select");
                    builder.AddAttribute(9, "class", "selector");
                    builder.AddAttribute(10, "value", GetSettingValue(settings, setting));
                    builder.AddAttribute(11, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, args => SetSettingValue(settings, setting.Key, args.Value?.ToString())));
                    foreach (var option in setting.Options)
                    {
                        builder.OpenElement(12, "option");
                        builder.AddAttribute(13, "value", option.Value);
                        builder.AddContent(14, option.Label);
                        builder.CloseElement();
                    }
                    builder.CloseElement();
                    break;
                default:
                    builder.OpenElement(8, "input");
                    builder.AddAttribute(9, "class", "mapping-input");
                    builder.AddAttribute(10, "type", setting.InputKind == "Password" ? "password" : setting.InputKind == "Number" ? "number" : "text");
                    builder.AddAttribute(11, "value", GetSettingValue(settings, setting));
                    builder.AddAttribute(12, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, args => SetSettingValue(settings, setting.Key, args.Value?.ToString())));
                    builder.CloseElement();
                    break;
            }

            if (!string.Equals(setting.InputKind, "Boolean", StringComparison.Ordinal))
            {
                builder.OpenElement(20, "div");
                builder.AddAttribute(21, "class", "module-description");
                builder.AddContent(22, setting.Description);
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        builder.CloseElement();
    };

    private static string GetSettingValue(Dictionary<string, string> settings, ModuleSettingViewModel setting)
        => settings.TryGetValue(setting.Key, out var value) ? value : setting.DefaultValue ?? string.Empty;

    private void SetSettingValue(Dictionary<string, string> settings, string key, string? value)
        => settings[key] = value ?? string.Empty;

    private static bool GetBooleanValue(ChangeEventArgs args) => args.Value is bool booleanValue && booleanValue;

    private void SetBooleanSettingValue(Dictionary<string, string> settings, string key, bool value)
        => settings[key] = value.ToString().ToLowerInvariant();

    private bool IsJoinNode(FlowNode node)
        => node.Stage == NodeStage.Augment && string.Equals(node.ModuleId, JoinModuleId, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLeftSourceValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? WorkingDocumentSelector : value;

    private static string CardinalityLabel(string cardinality) => cardinality switch
    {
        "Nested" => "Nested (one record per left row)",
        _ => "Fan out (one record per matched pair)"
    };

    private string JoinSideName(FlowNode node, string side)
    {
        if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase))
        {
            var rightSourceId = node.Settings.GetValueOrDefault(RightSourceKey);
            return string.IsNullOrWhiteSpace(rightSourceId) ? "right source" : rightSourceId;
        }

        var leftSourceId = NormalizeLeftSourceValue(node.Settings.GetValueOrDefault(LeftSourceKey));
        return string.Equals(leftSourceId, WorkingDocumentSelector, StringComparison.Ordinal) ? "working document" : leftSourceId;
    }

    private string JoinSideLabel(FlowNode node, string sourceKind)
        => string.Equals(sourceKind, "Literal", StringComparison.OrdinalIgnoreCase)
            ? "Literal"
            : $"{sourceKind} ({JoinSideName(node, sourceKind)})";

    private JoinEditorState EnsureJoinEditor(FlowNode node)
    {
        if (_joinEditors.TryGetValue(node.Id, out var existing))
        {
            return existing;
        }

        var state = new JoinEditorState();
        state.Keys.AddRange(DeserializeJoinKeys(node.Settings.GetValueOrDefault(JoinKeysKey)));
        state.Mappings.AddRange(DeserializeJoinMappings(node.Settings.GetValueOrDefault(MappingsKey)));
        _joinEditors[node.Id] = state;
        return state;
    }

    private void SetJoinSetting(FlowNode node, string key, string? value)
    {
        node.Settings[key] = value ?? string.Empty;
    }

    private void AddJoinKey(FlowNode node)
    {
        var state = EnsureJoinEditor(node);
        state.Keys.Add(new JoinKeyRow());
        WriteJoinKeys(node, state);
    }

    private void RemoveJoinKey(FlowNode node, JoinKeyRow row)
    {
        var state = EnsureJoinEditor(node);
        state.Keys.Remove(row);
        WriteJoinKeys(node, state);
    }

    private void SetJoinKeyPath(FlowNode node, JoinKeyRow row, bool isLeft, string? value)
    {
        if (isLeft)
        {
            row.LeftPath = value ?? string.Empty;
        }
        else
        {
            row.RightPath = value ?? string.Empty;
        }

        WriteJoinKeys(node, EnsureJoinEditor(node));
    }

    private void AddJoinMapping(FlowNode node)
    {
        var state = EnsureJoinEditor(node);
        state.Mappings.Add(new FieldMappingRow { SourceKind = "Left", Condition = "Always" });
        WriteJoinMappings(node, state);
    }

    private void RemoveJoinMapping(FlowNode node, FieldMappingRow row)
    {
        var state = EnsureJoinEditor(node);
        state.Mappings.Remove(row);
        WriteJoinMappings(node, state);
    }

    private void SetJoinMappingField(FlowNode node, FieldMappingRow row, JoinMappingField field, string? value)
    {
        var text = value ?? string.Empty;
        switch (field)
        {
            case JoinMappingField.SourceKind:
                row.SourceKind = string.IsNullOrWhiteSpace(text) ? "Left" : text;
                break;
            case JoinMappingField.SourcePath:
                row.SourcePath = text;
                break;
            case JoinMappingField.TargetField:
                row.TargetField = text;
                break;
            case JoinMappingField.Condition:
                row.Condition = string.IsNullOrWhiteSpace(text) ? "Always" : text;
                break;
            default:
                row.LiteralValue = text;
                break;
        }

        WriteJoinMappings(node, EnsureJoinEditor(node));
    }

    private static void WriteJoinKeys(FlowNode node, JoinEditorState state)
    {
        var keys = state.Keys
            .Select(static row => new JoinKeyDto { LeftPath = row.LeftPath, RightPath = row.RightPath })
            .ToArray();
        node.Settings[JoinKeysKey] = JsonSerializer.Serialize(keys, JsonOptions);
    }

    private static void WriteJoinMappings(FlowNode node, JoinEditorState state)
    {
        var mappings = state.Mappings
            .Select(static row => new JoinMappingDto
            {
                SourceKind = row.SourceKind,
                SourcePath = row.SourcePath,
                TargetField = row.TargetField,
                Condition = row.Condition,
                LiteralValue = string.IsNullOrWhiteSpace(row.LiteralValue) ? null : row.LiteralValue
            })
            .ToArray();
        node.Settings[MappingsKey] = JsonSerializer.Serialize(mappings, JsonOptions);
    }

    private static List<JoinKeyRow> DeserializeJoinKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<JoinKeyDto>>(json, JsonOptions) ?? [])
                .Select(static key => new JoinKeyRow { LeftPath = key.LeftPath ?? string.Empty, RightPath = key.RightPath ?? string.Empty })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<FieldMappingRow> DeserializeJoinMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<JoinMappingDto>>(json, JsonOptions) ?? [])
                .Select(static mapping => new FieldMappingRow
                {
                    SourceKind = string.IsNullOrWhiteSpace(mapping.SourceKind) ? "Left" : mapping.SourceKind,
                    SourcePath = mapping.SourcePath ?? string.Empty,
                    TargetField = mapping.TargetField ?? string.Empty,
                    Condition = string.IsNullOrWhiteSpace(mapping.Condition) ? "Always" : mapping.Condition,
                    LiteralValue = mapping.LiteralValue ?? string.Empty
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
