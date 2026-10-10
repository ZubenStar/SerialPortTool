using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace SerialPortTool.Models;

/// <summary>
/// 端口列表里的一行：串口名 + 设备标识 + 用户备注。
/// </summary>
/// <remarks>
/// <para>
/// Introduced in v2.5.0 to replace the bare <c>string</c> the left-hand list used to bind to. The list had
/// three things to say about a port (its name, what device is behind it, and what the user calls it) and
/// only one of them fits in a string.
/// </para>
/// <para>
/// Every derived string is computed here rather than in XAML: a <c>DataTemplate</c> that builds its second
/// line from three converters is unreadable, and the same strings are needed by the F2 palette anyway —
/// one project, both consumers.
/// </para>
/// <para>
/// <see cref="ToString"/> returns the port name because that is what any future picker without an
/// <c>ItemTemplate</c> would render; see the note on <c>SerialParameterOption&lt;T&gt;.ToString</c> for why
/// a type in a bound collection has to be able to degrade that way.
/// </para>
/// </remarks>
public partial class AvailablePortItem : ObservableObject
{
    public AvailablePortItem(string portName)
    {
        PortName = portName;
    }

    /// <summary>串口名。构造后不再变化——它是这一行的身份。</summary>
    public string PortName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLine))]
    [NotifyPropertyChangedFor(nameof(HasDeviceDetails))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _manufacturer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLine))]
    [NotifyPropertyChangedFor(nameof(HasDeviceDetails))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLine))]
    [NotifyPropertyChangedFor(nameof(HasDeviceDetails))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _vendorId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLine))]
    [NotifyPropertyChangedFor(nameof(HasDeviceDetails))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _productId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLine))]
    [NotifyPropertyChangedFor(nameof(HasDeviceDetails))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _serialNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetadataLine))]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _notes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetadataLine))]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _tags = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    [NotifyPropertyChangedFor(nameof(SearchText))]
    private string _group = string.Empty;

    /// <summary>True when any device identifier could be resolved, so the row earns a second line.</summary>
    public bool HasDeviceDetails => DeviceLine.Length > 0;

    /// <summary>True when the user wrote a note / tag / group for this port.</summary>
    public bool HasMetadata =>
        Notes.Length > 0 || Tags.Length > 0 || Group.Length > 0;

    /// <summary>
    /// 第二行：设备描述与 VID/PID。
    /// </summary>
    public string DeviceLine => BuildDeviceLine();

    /// <summary>
    /// 第三行：备注与标签。
    /// </summary>
    public string MetadataLine => BuildMetadataLine();

    /// <summary>
    /// 悬停详情：行里放不下的完整内容，鼠标停在端口行上时由 Tooltip 呈现。
    /// </summary>
    /// <remarks>
    /// 左栏刻意窄（宽度让给日志），第二、三行以省略号截断是常态而非异常，完整文本必须另有出口。
    /// 序列号也放这里：它最能区分两个外观相同的适配器，却挤不进任何一行。每个字段一行、空值跳过；
    /// <see cref="PortName"/> 恒非空，所以结果至少一行，Tooltip 不会拿到空字符串。
    /// </remarks>
    public string ToolTipText => BuildToolTipText();

    /// <summary>
    /// 命令面板（F2）搜索用的文本；刻意包含所有字段，因为用户会照着备注找口。
    /// </summary>
    public string SearchText => $"{PortName} {DeviceLine} {MetadataLine} {Group}";

    /// <summary>
    /// 套用设备身份信息（保留调用方已经填好的备注不动）。
    /// </summary>
    public void ApplyDeviceInfo(PortDeviceInfo info)
    {
        Manufacturer = info.Manufacturer;
        Description = info.Description;
        VendorId = info.VendorId;
        ProductId = info.ProductId;
        SerialNumber = info.SerialNumber;
    }

    /// <summary>套用备注/标签/分组。</summary>
    public void ApplyMetadata(PortMetadata metadata)
    {
        Notes = metadata.Notes;
        Tags = metadata.Tags;
        Group = metadata.Group;
    }

    private string BuildDeviceLine()
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(Manufacturer))
        {
            parts.Add(Manufacturer!);
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            parts.Add(Description!);
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        if (!string.IsNullOrWhiteSpace(VendorId) || !string.IsNullOrWhiteSpace(ProductId))
        {
            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append($"VID:{VendorId ?? "-"} PID:{ProductId ?? "-"}");
        }

        return builder.ToString();
    }

    private string BuildMetadataLine()
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(Notes))
        {
            builder.Append(Notes);
        }

        if (!string.IsNullOrWhiteSpace(Tags))
        {
            if (builder.Length > 0)
            {
                builder.Append("  ");
            }

            builder.Append('#').Append(Tags);
        }

        return builder.ToString();
    }

    private string BuildToolTipText()
    {
        var builder = new StringBuilder();
        AppendToolTipLine(builder, PortName);
        AppendToolTipLine(builder, DeviceLine);

        if (!string.IsNullOrWhiteSpace(SerialNumber))
        {
            AppendToolTipLine(builder, $"SN：{SerialNumber}");
        }

        AppendToolTipLine(builder, MetadataLine);
        return builder.ToString();
    }

    /// <summary>追加一个非空行；已有多行时先补换行，空行直接跳过。</summary>
    private static void AppendToolTipLine(StringBuilder builder, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(line);
    }

    /// <summary>The port name, so a picker without an <c>ItemTemplate</c> degrades to something readable.</summary>
    public override string ToString() => PortName;
}
