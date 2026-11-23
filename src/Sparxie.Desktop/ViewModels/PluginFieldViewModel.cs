using System.Text.Json.Nodes;
using Sparxie.Infrastructure.Plugins;

namespace Sparxie.Desktop.ViewModels;

/// <summary>
/// 插件设置里的一个字段，对应 settingsSchema 的一项。四种控件（开关/数字/下拉/文本）用同一个视图模型，
/// 界面模板按 <see cref="IsBool"/> 等标志选择显示哪个控件。值的合法性由 schema 的归一化兜底（见 <see cref="PluginSettingsSchema.Normalize"/>），
/// 所以这里不抛异常、不弹错误：数字清空（NaN）按默认值处理。
/// </summary>
public sealed class PluginFieldViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _boolValue;
    private double _numberValue;
    private string _textValue = string.Empty;
    private bool _isVisible = true;

    public PluginFieldViewModel(PluginField field, JsonNode? initial, Action changed)
    {
        Field = field;
        _changed = changed;
        var seed = initial ?? field.Default;
        switch (field.Kind)
        {
            case PluginFieldKind.Bool:
                _boolValue = seed is JsonValue b && b.TryGetValue<bool>(out var boolValue) ? boolValue : field.Default.GetValue<bool>();
                break;
            case PluginFieldKind.Int:
                _numberValue = seed is JsonValue n && n.TryGetValue<int>(out var intValue) ? intValue : field.Default.GetValue<int>();
                break;
            default:
                _textValue = seed is JsonValue t && t.TryGetValue<string>(out var text) ? text : field.Default.GetValue<string>();
                break;
        }
    }

    public PluginField Field { get; }

    public string Key => Field.Key;

    public string? Group => Field.Group;

    /// <summary>分组标题：只有同一分组的第一个字段才有值（由编辑器在构建列表时设置），其余为空。</summary>
    public string GroupHeader { get; set; } = string.Empty;

    public bool HasGroupHeader => GroupHeader.Length > 0;

    /// <summary>当前值是否等于默认值（决定“恢复默认”是否可用）。</summary>
    public bool IsDefault => Field.Kind switch
    {
        PluginFieldKind.Bool => _boolValue == Field.Default.GetValue<bool>(),
        PluginFieldKind.Int => !double.IsNaN(_numberValue) && (int)Math.Round(_numberValue) == Field.Default.GetValue<int>(),
        _ => string.Equals(_textValue, Field.Default.GetValue<string>(), StringComparison.Ordinal),
    };

    public bool CanReset => !IsDefault;

    /// <summary>恢复这个字段的默认值。</summary>
    public void ResetToDefault()
    {
        switch (Field.Kind)
        {
            case PluginFieldKind.Bool:
                BoolValue = Field.Default.GetValue<bool>();
                break;
            case PluginFieldKind.Int:
                NumberValue = Field.Default.GetValue<int>();
                break;
            default:
                TextValue = Field.Default.GetValue<string>();
                break;
        }
    }

    public RelayCommand ResetCommand => _resetCommand ??= new RelayCommand(ResetToDefault);

    private RelayCommand? _resetCommand;

    private void NotifyDefaultChanged()
    {
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(CanReset));
    }

    public string Label => Field.Label;

    public string? Hint => Field.Hint;

    public bool HasHint => !string.IsNullOrEmpty(Field.Hint);

    public bool IsBool => Field.Kind == PluginFieldKind.Bool;

    public bool IsNumber => Field.Kind == PluginFieldKind.Int;

    public bool IsChoice => Field.Kind == PluginFieldKind.Enum;

    public bool IsText => Field.Kind == PluginFieldKind.String;

    public IReadOnlyList<PluginFieldOption> Options => Field.Options;

    public double Minimum => Field.Min;

    public double Maximum => Field.Max;

    public double Step => Field.Step;

    /// <summary>自动生成的范围说明（整数字段）：让用户知道能填什么，而不是填错后被静默改掉。</summary>
    public string RangeText => Field.Kind == PluginFieldKind.Int
        ? (Field.Step > 1 ? $"范围 {Field.Min:0}–{Field.Max:0}，步长 {Field.Step:0}" : $"范围 {Field.Min:0}–{Field.Max:0}")
        : string.Empty;

    public bool HasRange => RangeText.Length > 0;

    public int MaxLength => Field.MaxLength;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (SetProperty(ref _boolValue, value))
            {
                NotifyDefaultChanged();
                _changed();
            }
        }
    }

    public double NumberValue
    {
        get => _numberValue;
        set
        {
            if (SetProperty(ref _numberValue, value))
            {
                NotifyDefaultChanged();
                _changed();
            }
        }
    }

    /// <summary>枚举：选中项的 value；字符串：输入的文本。</summary>
    public string TextValue
    {
        get => _textValue;
        set
        {
            if (SetProperty(ref _textValue, value ?? string.Empty))
            {
                NotifyDefaultChanged();
                _changed();
            }
        }
    }

    public JsonNode ToJson() => Field.Kind switch
    {
        PluginFieldKind.Bool => JsonValue.Create(_boolValue)!,
        PluginFieldKind.Int => JsonValue.Create(double.IsNaN(_numberValue) || double.IsInfinity(_numberValue) ? Field.Default.GetValue<int>() : (int)Math.Round(_numberValue))!,
        _ => JsonValue.Create(_textValue)!,
    };
}
