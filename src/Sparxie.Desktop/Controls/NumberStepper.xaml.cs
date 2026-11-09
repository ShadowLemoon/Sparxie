using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sparxie.Desktop.Controls;

public sealed partial class NumberStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberStepper),
        new PropertyMetadata(0.0, (d, _) => ((NumberStepper)d).SyncBox()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumberStepper),
        new PropertyMetadata(double.MinValue, (d, _) => ((NumberStepper)d).SyncBox()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumberStepper),
        new PropertyMetadata(double.MaxValue, (d, _) => ((NumberStepper)d).SyncBox()));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumberStepper), new PropertyMetadata(1.0));

    public NumberStepper()
    {
        InitializeComponent();
        SyncBox();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    private void SyncBox()
    {
        if (Box is null)
        {
            return;
        }

        Box.Minimum = Minimum;
        Box.Maximum = Maximum;
        if (!Equals(Box.Value, Value))
        {
            Box.Value = Value;
        }

        DecButton.IsEnabled = Value > Minimum;
        IncButton.IsEnabled = Value < Maximum;
    }

    private void OnBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // 清空或非法输入时 NumberBox 给 NaN：忽略，保持上一个有效值，避免 NaN 传入域层。
        if (!double.IsNaN(args.NewValue) && !Equals(Value, args.NewValue))
        {
            Value = args.NewValue;
        }
    }

    private void OnDecrement(object sender, RoutedEventArgs e) => Value = Math.Max(Minimum, Value - Step);

    private void OnIncrement(object sender, RoutedEventArgs e) => Value = Math.Min(Maximum, Value + Step);
}
