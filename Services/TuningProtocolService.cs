using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Services;

public sealed class TuningProtocolService : ITuningProtocolService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<TuningProtocolService> _logger;

    public TuningProtocolService(ILogger<TuningProtocolService> logger)
    {
        _logger = logger;
    }

    public async Task<TuningProtocolDescriptor> LoadDescriptorAsync(
        string descriptorPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
        {
            throw new TuningProtocolException("未选择 tuning JSON 描述文件");
        }

        if (!File.Exists(descriptorPath))
        {
            throw new TuningProtocolException($"tuning JSON 描述文件不存在: {descriptorPath}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(descriptorPath, cancellationToken);
            var descriptor = JsonSerializer.Deserialize<TuningProtocolDescriptor>(json, JsonOptions)
                ?? throw new TuningProtocolException("tuning JSON 描述文件为空或格式无效");

            descriptor.SourcePath = descriptorPath;
            ValidateDescriptor(descriptor);
            _logger.LogInformation("Loaded tuning descriptor {Name} from {Path}", descriptor.Name, descriptorPath);
            return descriptor;
        }
        catch (JsonException ex)
        {
            throw new TuningProtocolException($"tuning JSON 解析失败: {ex.Message}", ex);
        }
    }

    public async Task<TuningBuildResult> BuildAsync(
        string binPath,
        TuningProtocolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binPath))
        {
            throw new TuningProtocolException("未选择 tuning bin 文件");
        }

        if (!File.Exists(binPath))
        {
            throw new TuningProtocolException($"tuning bin 文件不存在: {binPath}");
        }

        ValidateDescriptor(descriptor);

        var fileBytes = await File.ReadAllBytesAsync(binPath, cancellationToken);
        if (fileBytes.Length == 0)
        {
            throw new TuningProtocolException("tuning bin 文件为空，不能打包发送");
        }

        var byteOrder = ParseByteOrder(descriptor.ByteOrder);
        var dspMessage = BuildDspMessage(descriptor, fileBytes, byteOrder);
        var packetPlan = BuildPacketPlan(descriptor, dspMessage, byteOrder);
        var sendSegments = new List<TuningSendSegment>(
            packetPlan.PacketFrames.Count + (descriptor.Tota.SendHeaderInfo ? 1 : 0));
        if (descriptor.Tota.SendHeaderInfo)
        {
            sendSegments.Add(new TuningSendSegment
            {
                Data = Combine(packetPlan.Header, packetPlan.InfoArea),
                IsPacket = false
            });
        }

        foreach (var packetFrame in packetPlan.PacketFrames)
        {
            sendSegments.Add(new TuningSendSegment
            {
                Data = packetFrame.Data,
                IsPacket = true
            });
        }

        var hash = ToHexString(SHA256.HashData(fileBytes));

        return new TuningBuildResult
        {
            BinFilePath = binPath,
            BinSha256 = hash,
            SendSegments = sendSegments,
            PacketCount = packetPlan.PacketFrames.Count,
            DspMessageLength = dspMessage.Length,
            HeaderInfoLength = packetPlan.Header.Length + packetPlan.InfoArea.Length,
            DelayBetweenPacketsMs = packetPlan.DelayBetweenPacketsMs
        };
    }

    public async Task<string> ComputeFileHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new TuningProtocolException("未选择 tuning bin 文件");
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return ToHexString(hash);
    }

    private static void ValidateDescriptor(TuningProtocolDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new TuningProtocolException("tuning JSON 描述文件为空");
        }

        _ = ParseByteOrder(descriptor.ByteOrder);

        if (descriptor.DspMessage == null)
        {
            throw new TuningProtocolException("tuning JSON 缺少 dspMessage 配置");
        }

        descriptor.DspMessage.Layout ??= new List<TuningProtocolField>();
        if (descriptor.DspMessage.Layout.Count == 0)
        {
            throw new TuningProtocolException("tuning JSON 缺少 dspMessage.layout");
        }

        if (!descriptor.DspMessage.Layout.Any(field => field != null && IsType(field, "fileBytes")))
        {
            throw new TuningProtocolException("dspMessage.layout 必须包含 type=fileBytes 字段");
        }

        if (descriptor.Tota == null)
        {
            throw new TuningProtocolException("tuning JSON 缺少 tota 配置");
        }

        descriptor.Parameters ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        descriptor.UartSend ??= new TuningUartSendDescriptor();

        if (descriptor.Tota.PacketFrame == null)
        {
            throw new TuningProtocolException("tuning JSON 缺少 tota.packetFrame 配置");
        }

        descriptor.Tota.HeaderLayout ??= new List<TuningProtocolField>();
        descriptor.Tota.InfoAreaFields ??= new List<JsonElement>();
        descriptor.Tota.PacketFrame.Layout ??= new List<TuningProtocolField>();

        if (string.IsNullOrWhiteSpace(descriptor.Tota.HeaderTagHex) &&
            descriptor.Tota.HeaderLayout.Count == 0)
        {
            throw new TuningProtocolException("tota 必须配置 headerTagHex 或 headerLayout");
        }

        if (!IsDefined(descriptor.Tota.PacketFrame.PacketLength))
        {
            throw new TuningProtocolException("tota.packetFrame 必须配置 packetLength");
        }

        if (!IsDefined(descriptor.Tota.PacketFrame.PayloadLength))
        {
            throw new TuningProtocolException("tota.packetFrame 必须配置 payloadLength");
        }

        if (descriptor.Tota.InfoAreaFields.Count == 0)
        {
            throw new TuningProtocolException("tota 必须配置 infoAreaFields");
        }

        foreach (var field in descriptor.DspMessage.Layout)
        {
            if (field == null)
            {
                throw new TuningProtocolException("dspMessage.layout 中存在空字段");
            }

            ValidateField(field, "dspMessage.layout");
        }

        foreach (var field in descriptor.Tota.HeaderLayout)
        {
            if (field == null)
            {
                throw new TuningProtocolException("tota.headerLayout 中存在空字段");
            }

            ValidateField(field, "tota.headerLayout");
        }

        foreach (var field in descriptor.Tota.PacketFrame.Layout)
        {
            if (field == null)
            {
                throw new TuningProtocolException("tota.packetFrame.layout 中存在空字段");
            }

            ValidateField(field, "tota.packetFrame.layout");
        }
    }

    private static void ValidateField(TuningProtocolField field, string location)
    {
        field.Inputs ??= new List<string>();

        if (string.IsNullOrWhiteSpace(field.Name))
        {
            throw new TuningProtocolException($"{location} 中存在缺少 name 的字段");
        }

        if (string.IsNullOrWhiteSpace(field.Type))
        {
            throw new TuningProtocolException($"{location}.{field.Name} 缺少 type");
        }

        if (IsType(field, "bytes") && string.IsNullOrWhiteSpace(field.Hex))
        {
            throw new TuningProtocolException($"{location}.{field.Name} 的 bytes 字段必须配置 hex");
        }

        if (field.Algorithm != null &&
            !field.Algorithm.Equals("onesComplementSum", StringComparison.OrdinalIgnoreCase))
        {
            throw new TuningProtocolException($"{location}.{field.Name} 使用了不支持的算法: {field.Algorithm}");
        }

        if (field.Algorithm != null && !IsType(field, "uint8"))
        {
            throw new TuningProtocolException($"{location}.{field.Name} 的 checksum 字段必须使用 type=uint8");
        }

        if (field.Algorithm != null && field.Inputs.Count == 0)
        {
            throw new TuningProtocolException($"{location}.{field.Name} 的 checksum 字段必须配置 inputs");
        }

        if (field.Inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new TuningProtocolException($"{location}.{field.Name} 的 inputs 不能包含空字段名");
        }
    }

    private static byte[] BuildDspMessage(
        TuningProtocolDescriptor descriptor,
        byte[] fileBytes,
        TuningByteOrder byteOrder)
    {
        var fieldSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.DspMessage.Layout)
        {
            fieldSizes[field.Name] = GetFixedOrDynamicFieldSize(field, fileBytes.Length, 0);
        }

        var context = new EvaluationContext(descriptor, fieldSizes);
        var fieldBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var message = new List<byte>();

        foreach (var field in descriptor.DspMessage.Layout)
        {
            byte[] bytes;
            if (field.Algorithm != null)
            {
                bytes = BuildChecksumField(field, fieldBytes);
            }
            else
            {
                bytes = BuildFieldBytes(field, context, fileBytes, Array.Empty<byte>(), byteOrder, $"dspMessage.{field.Name}");
            }

            fieldBytes[field.Name] = bytes;
            context.FieldBytes[field.Name] = bytes;
            message.AddRange(bytes);
        }

        return message.ToArray();
    }

    private static TuningPacketPlan BuildPacketPlan(
        TuningProtocolDescriptor descriptor,
        byte[] dspMessage,
        TuningByteOrder byteOrder)
    {
        var baseContext = new EvaluationContext(descriptor, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
        {
            Variables =
            {
                ["dspTotalLength"] = dspMessage.Length
            }
        };

        var packetLength = EvaluateInt(descriptor.Tota.PacketFrame.PacketLength, baseContext, "tota.packetFrame.packetLength");
        var payloadLength = EvaluateInt(descriptor.Tota.PacketFrame.PayloadLength, baseContext, "tota.packetFrame.payloadLength");
        if (payloadLength <= 0)
        {
            throw new TuningProtocolException("tota.packetFrame.payloadLength 必须大于 0");
        }

        if (packetLength <= payloadLength)
        {
            throw new TuningProtocolException("tota.packetFrame.packetLength 必须大于 payloadLength");
        }

        var waitTimeValue = EvaluateOptionalInt(descriptor.Tota.WaitTime, baseContext, "tota.waitTime");
        if (!waitTimeValue.HasValue && InfoAreaUsesWaitTime(descriptor.Tota.InfoAreaFields))
        {
            throw new TuningProtocolException("tota.infoAreaFields 使用 waitTime 时，必须配置 tota.waitTime");
        }

        var waitTime = waitTimeValue ?? 0;
        var delayBetweenPacketsMs = EvaluateOptionalInt(
            descriptor.UartSend.DelayBetweenPacketsMs,
            baseContext,
            "uartSend.delayBetweenPacketsMs") ?? waitTime;

        var packetFrames = new List<TuningPacketFrame>();
        for (int position = 0, index = 0; position < dspMessage.Length; position += payloadLength, index++)
        {
            var sliceLength = Math.Min(payloadLength, dspMessage.Length - position);
            var slice = new byte[sliceLength];
            Array.Copy(dspMessage, position, slice, 0, sliceLength);

            var frame = descriptor.Tota.PacketFrame.Layout.Count > 0
                ? BuildPacketFrameFromLayout(descriptor, slice, index, position, dspMessage.Length, byteOrder)
                : BuildStandardPacketFrame(descriptor, slice, position, dspMessage.Length, byteOrder);

            if (frame.Length > packetLength)
            {
                throw new TuningProtocolException(
                    $"TOTA packet 长度 {frame.Length} 超过 packetLength {packetLength}");
            }

            packetFrames.Add(new TuningPacketFrame(index, position, frame));
        }

        var infoArea = BuildInfoArea(descriptor, packetFrames, waitTime, byteOrder);
        var totalTotaSize = CalculateHeaderLength(descriptor, byteOrder) + infoArea.Length + packetFrames.Sum(packet => packet.Data.Length);
        var header = BuildTotaHeader(descriptor, totalTotaSize, packetFrames.Count, infoArea.Length, byteOrder);

        return new TuningPacketPlan(header, infoArea, packetFrames, delayBetweenPacketsMs);
    }

    private static byte[] BuildStandardPacketFrame(
        TuningProtocolDescriptor descriptor,
        byte[] slice,
        int position,
        int dspTotalLength,
        TuningByteOrder byteOrder)
    {
        if (!IsDefined(descriptor.Tota.PacketFrame.CommandType))
        {
            throw new TuningProtocolException(
                "tota.packetFrame 未配置 layout 时，必须配置 commandType");
        }

        var context = new EvaluationContext(descriptor, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
        {
            Variables =
            {
                ["sliceLength"] = slice.Length,
                ["position"] = position,
                ["dspPosition"] = position,
                ["dspTotalLength"] = dspTotalLength
            }
        };

        var commandType = EvaluateInt(descriptor.Tota.PacketFrame.CommandType, context, "tota.packetFrame.commandType");
        var packetDataLength = slice.Length + sizeof(ushort) + sizeof(ushort);

        var bytes = new List<byte>(sizeof(ushort) * 4 + slice.Length);
        bytes.AddRange(SerializeUInt16(commandType, byteOrder, "tota.packetFrame.commandType"));
        bytes.AddRange(SerializeUInt16(packetDataLength, byteOrder, "tota.packetFrame.packetDataLength"));
        bytes.AddRange(SerializeUInt16(dspTotalLength, byteOrder, "tota.packetFrame.dspTotalLength"));
        bytes.AddRange(SerializeUInt16(position, byteOrder, "tota.packetFrame.position"));
        bytes.AddRange(slice);
        return bytes.ToArray();
    }

    private static bool InfoAreaUsesWaitTime(IEnumerable<JsonElement> infoAreaFields)
    {
        foreach (var infoField in infoAreaFields)
        {
            if (infoField.ValueKind == JsonValueKind.Object &&
                infoField.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                var objectFieldName = nameElement.GetString()?.Trim();
                if (IsWaitTimeFieldName(objectFieldName))
                {
                    return true;
                }
            }

            if (infoField.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var fieldName = infoField.GetString()?.Trim();
            if (IsWaitTimeFieldName(fieldName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWaitTimeFieldName(string? fieldName)
    {
        return fieldName != null &&
               (fieldName.Equals("waitTime", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("wait_time", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("waitTimeBeforeNextPacket", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] BuildPacketFrameFromLayout(
        TuningProtocolDescriptor descriptor,
        byte[] slice,
        int index,
        int position,
        int dspTotalLength,
        TuningByteOrder byteOrder)
    {
        var fieldSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Tota.PacketFrame.Layout)
        {
            fieldSizes[field.Name] = GetFixedOrDynamicFieldSize(field, 0, slice.Length);
        }

        var packetFrameLength = fieldSizes.Values.Sum();
        var context = new EvaluationContext(descriptor, fieldSizes)
        {
            Variables =
            {
                ["index"] = index,
                ["position"] = position,
                ["dspPosition"] = position,
                ["sliceLength"] = slice.Length,
                ["dspTotalLength"] = dspTotalLength,
                ["packetFrameLength"] = packetFrameLength
            }
        };

        var fieldBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var bytes = new List<byte>(packetFrameLength);
        foreach (var field in descriptor.Tota.PacketFrame.Layout)
        {
            var fieldData = BuildFieldBytes(
                field,
                context,
                Array.Empty<byte>(),
                slice,
                byteOrder,
                $"tota.packetFrame.{field.Name}");
            fieldBytes[field.Name] = fieldData;
            context.FieldBytes[field.Name] = fieldData;
            bytes.AddRange(fieldData);
        }

        return bytes.ToArray();
    }

    private static byte[] BuildInfoArea(
        TuningProtocolDescriptor descriptor,
        IReadOnlyList<TuningPacketFrame> packetFrames,
        int waitTime,
        TuningByteOrder byteOrder)
    {
        var bytes = new List<byte>();
        foreach (var packetFrame in packetFrames)
        {
            foreach (var infoField in descriptor.Tota.InfoAreaFields)
            {
                if (infoField.ValueKind == JsonValueKind.String)
                {
                    var fieldName = infoField.GetString() ?? string.Empty;
                    var value = fieldName.Trim().ToLowerInvariant() switch
                    {
                        "index" => packetFrame.Index + descriptor.Tota.PacketIndexBase,
                        "position" => packetFrame.Position,
                        "length" => packetFrame.Data.Length,
                        "waittime" => waitTime,
                        "wait_time" => waitTime,
                        "waittimebeforenextpacket" => waitTime,
                        _ => throw new TuningProtocolException($"tota.infoAreaFields 包含未知字段: {fieldName}")
                    };

                    bytes.AddRange(SerializeUInt16(value, byteOrder, $"tota.infoAreaFields.{fieldName}"));
                    continue;
                }

                if (infoField.ValueKind == JsonValueKind.Object)
                {
                    var field = infoField.Deserialize<TuningProtocolField>(JsonOptions)
                        ?? throw new TuningProtocolException("tota.infoAreaFields 中存在无效字段对象");
                    var fieldSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        [field.Name] = GetFixedOrDynamicFieldSize(field, 0, 0)
                    };
                    var context = new EvaluationContext(descriptor, fieldSizes)
                    {
                        Variables =
                        {
                            ["index"] = packetFrame.Index + descriptor.Tota.PacketIndexBase,
                            ["position"] = packetFrame.Position,
                            ["packetPosition"] = packetFrame.Position,
                            ["length"] = packetFrame.Data.Length,
                            ["packetLength"] = packetFrame.Data.Length,
                            ["waitTime"] = waitTime
                        }
                    };
                    var fieldData = BuildFieldBytes(
                        field,
                        context,
                        Array.Empty<byte>(),
                        Array.Empty<byte>(),
                        byteOrder,
                        $"tota.infoAreaFields.{field.Name}");
                    bytes.AddRange(fieldData);
                    continue;
                }

                throw new TuningProtocolException("tota.infoAreaFields 仅支持字符串或字段对象");
            }
        }

        return bytes.ToArray();
    }

    private static int CalculateHeaderLength(TuningProtocolDescriptor descriptor, TuningByteOrder byteOrder)
    {
        if (descriptor.Tota.HeaderLayout.Count > 0)
        {
            return descriptor.Tota.HeaderLayout.Sum(field => GetFixedOrDynamicFieldSize(field, 0, 0));
        }

        _ = byteOrder;
        return ParseHex(descriptor.Tota.HeaderTagHex, "tota.headerTagHex").Length + sizeof(ushort) + sizeof(ushort);
    }

    private static byte[] BuildTotaHeader(
        TuningProtocolDescriptor descriptor,
        int totalTotaSize,
        int packetCount,
        int infoAreaLength,
        TuningByteOrder byteOrder)
    {
        if (descriptor.Tota.HeaderLayout.Count == 0)
        {
            var headerBytes = new List<byte>();
            headerBytes.AddRange(ParseHex(descriptor.Tota.HeaderTagHex, "tota.headerTagHex"));
            headerBytes.AddRange(SerializeUInt16(totalTotaSize, byteOrder, "tota.totalSize"));
            headerBytes.AddRange(SerializeUInt16(packetCount, byteOrder, "tota.packetCount"));
            return headerBytes.ToArray();
        }

        var fieldSizes = descriptor.Tota.HeaderLayout.ToDictionary(
            field => field.Name,
            field => GetFixedOrDynamicFieldSize(field, 0, 0),
            StringComparer.OrdinalIgnoreCase);
        var context = new EvaluationContext(descriptor, fieldSizes)
        {
            Variables =
            {
                ["totalTotaSize"] = totalTotaSize,
                ["totalSize"] = totalTotaSize,
                ["packetCount"] = packetCount,
                ["infoAreaLength"] = infoAreaLength
            }
        };

        var fieldBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var layoutBytes = new List<byte>();
        foreach (var field in descriptor.Tota.HeaderLayout)
        {
            var fieldData = BuildFieldBytes(
                field,
                context,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                byteOrder,
                $"tota.header.{field.Name}");
            fieldBytes[field.Name] = fieldData;
            context.FieldBytes[field.Name] = fieldData;
            layoutBytes.AddRange(fieldData);
        }

        return layoutBytes.ToArray();
    }

    private static byte[] BuildFieldBytes(
        TuningProtocolField field,
        EvaluationContext context,
        byte[] fileBytes,
        byte[] sliceBytes,
        TuningByteOrder byteOrder,
        string location)
    {
        if (IsType(field, "bytes"))
        {
            return ParseHex(field.Hex, $"{location}.hex");
        }

        if (IsType(field, "fileBytes"))
        {
            return fileBytes;
        }

        if (IsType(field, "slice"))
        {
            return sliceBytes;
        }

        if (IsType(field, "uint8"))
        {
            var value = GetFieldNumericValue(field, context, location);
            return new[] { SerializeUInt8(value, location) };
        }

        if (IsType(field, "uint16"))
        {
            var value = GetFieldNumericValue(field, context, location);
            var fieldByteOrder = string.IsNullOrWhiteSpace(field.ByteOrder)
                ? byteOrder
                : ParseByteOrder(field.ByteOrder);
            return SerializeUInt16(value, fieldByteOrder, location);
        }

        throw new TuningProtocolException($"{location} 使用了不支持的字段类型: {field.Type}");
    }

    private static int GetFieldNumericValue(TuningProtocolField field, EvaluationContext context, string location)
    {
        if (IsDefined(field.Value))
        {
            return EvaluateInt(field.Value, context, $"{location}.value");
        }

        if (context.Variables.TryGetValue(field.Name, out var variableValue))
        {
            return variableValue;
        }

        throw new TuningProtocolException($"{location} 缺少 value");
    }

    private static byte[] BuildChecksumField(
        TuningProtocolField field,
        IDictionary<string, byte[]> fieldBytes)
    {
        if (!field.Algorithm!.Equals("onesComplementSum", StringComparison.OrdinalIgnoreCase))
        {
            throw new TuningProtocolException($"不支持的 checksum 算法: {field.Algorithm}");
        }

        if (field.Inputs.Count == 0)
        {
            throw new TuningProtocolException($"checksum 字段 {field.Name} 必须配置 inputs");
        }

        var sum = 0;
        foreach (var inputName in field.Inputs)
        {
            if (!fieldBytes.TryGetValue(inputName, out var inputBytes))
            {
                throw new TuningProtocolException($"checksum 输入字段不存在或尚未生成: {inputName}");
            }

            foreach (var b in inputBytes)
            {
                sum += b;
            }
        }

        return new[] { (byte)(~sum & 0xFF) };
    }

    private static int GetFixedOrDynamicFieldSize(TuningProtocolField field, int fileLength, int sliceLength)
    {
        if (IsType(field, "bytes"))
        {
            return ParseHex(field.Hex, $"{field.Name}.hex").Length;
        }

        if (IsType(field, "uint8"))
        {
            return sizeof(byte);
        }

        if (IsType(field, "uint16"))
        {
            return sizeof(ushort);
        }

        if (IsType(field, "fileBytes"))
        {
            return fileLength;
        }

        if (IsType(field, "slice"))
        {
            return sliceLength;
        }

        throw new TuningProtocolException($"字段 {field.Name} 使用了不支持的类型: {field.Type}");
    }

    private static int? EvaluateOptionalInt(JsonElement element, EvaluationContext context, string location)
    {
        return IsDefined(element) ? EvaluateInt(element, context, location) : null;
    }

    private static int EvaluateInt(JsonElement element, EvaluationContext context, string location)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.String => EvaluateExpression(element.GetString() ?? string.Empty, context, location),
            _ => throw new TuningProtocolException($"{location} 必须是数字或表达式字符串")
        };
    }

    private static int EvaluateExpression(string expression, EvaluationContext context, string location)
    {
        var compact = expression.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(compact))
        {
            throw new TuningProtocolException($"{location} 表达式为空");
        }

        var total = 0;
        var sign = 1;
        var index = 0;
        while (index < compact.Length)
        {
            if (compact[index] == '+')
            {
                sign = 1;
                index++;
                continue;
            }

            if (compact[index] == '-')
            {
                sign = -1;
                index++;
                continue;
            }

            var termStart = index;
            var depth = 0;
            while (index < compact.Length)
            {
                var ch = compact[index];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }
                else if (depth == 0 && (ch == '+' || ch == '-'))
                {
                    break;
                }

                index++;
            }

            var term = compact[termStart..index];
            total += sign * EvaluateTerm(term, context, location);
            sign = 1;
        }

        return total;
    }

    private static int EvaluateTerm(string term, EvaluationContext context, string location)
    {
        if (term.StartsWith("sizeof(", StringComparison.OrdinalIgnoreCase) && term.EndsWith(')'))
        {
            var name = term["sizeof(".Length..^1];
            if (context.FieldBytes.TryGetValue(name, out var bytes))
            {
                return bytes.Length;
            }

            if (context.FieldSizes.TryGetValue(name, out var size))
            {
                return size;
            }

            if (context.Variables.TryGetValue($"{name}Length", out var variableLength))
            {
                return variableLength;
            }

            throw new TuningProtocolException($"{location} 引用了未知 sizeof 字段: {name}");
        }

        if (term.StartsWith('$'))
        {
            var parameterName = term[1..];
            if (!TryGetParameter(context.Descriptor, parameterName, out var parameterElement))
            {
                throw new TuningProtocolException($"{location} 引用了未知参数: {parameterName}");
            }

            return EvaluateInt(parameterElement, context, $"{location}.${parameterName}");
        }

        if (context.Variables.TryGetValue(term, out var variableValue))
        {
            return variableValue;
        }

        if (TryParseIntegerLiteral(term, out var literalValue))
        {
            return literalValue;
        }

        throw new TuningProtocolException($"{location} 包含不支持的表达式项: {term}");
    }

    private static bool TryGetParameter(
        TuningProtocolDescriptor descriptor,
        string parameterName,
        out JsonElement parameterElement)
    {
        if (descriptor.Parameters.TryGetValue(parameterName, out parameterElement))
        {
            return true;
        }

        foreach (var kvp in descriptor.Parameters)
        {
            if (kvp.Key.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                parameterElement = kvp.Value;
                return true;
            }
        }

        parameterElement = default;
        return false;
    }

    private static bool TryParseIntegerLiteral(string text, out int value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static byte SerializeUInt8(int value, string location)
    {
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            throw new TuningProtocolException($"{location} 的 uint8 值超出范围: {value}");
        }

        return (byte)value;
    }

    private static byte[] SerializeUInt16(int value, TuningByteOrder byteOrder, string location)
    {
        if (value is < ushort.MinValue or > ushort.MaxValue)
        {
            throw new TuningProtocolException($"{location} 的 uint16 值超出范围: {value}");
        }

        var bytes = new byte[sizeof(ushort)];
        if (byteOrder == TuningByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        }

        return bytes;
    }

    private static byte[] ParseHex(string? hex, string location)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new TuningProtocolException($"{location} 不能为空");
        }

        var compact = hex
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        if (compact.Length % 2 != 0)
        {
            throw new TuningProtocolException($"{location} 的 hex 长度必须是偶数");
        }

        var bytes = new byte[compact.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(compact.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new TuningProtocolException($"{location} 包含非法 hex: {compact.Substring(i * 2, 2)}");
            }
        }

        return bytes;
    }

    private static TuningByteOrder ParseByteOrder(string byteOrder)
    {
        if (string.Equals(byteOrder, "littleEndian", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(byteOrder, "little", StringComparison.OrdinalIgnoreCase))
        {
            return TuningByteOrder.LittleEndian;
        }

        if (string.Equals(byteOrder, "bigEndian", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(byteOrder, "big", StringComparison.OrdinalIgnoreCase))
        {
            return TuningByteOrder.BigEndian;
        }

        throw new TuningProtocolException($"不支持的 byteOrder: {byteOrder}");
    }

    private static bool IsDefined(JsonElement element)
    {
        return element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
    }

    private static bool IsType(TuningProtocolField field, string type)
    {
        return string.Equals(field.Type, type, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var bytes = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, bytes, 0, first.Length);
        Buffer.BlockCopy(second, 0, bytes, first.Length, second.Length);
        return bytes;
    }

    private static string ToHexString(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class EvaluationContext
    {
        public EvaluationContext(TuningProtocolDescriptor descriptor, IDictionary<string, int> fieldSizes)
        {
            Descriptor = descriptor;
            FieldSizes = fieldSizes;
        }

        public TuningProtocolDescriptor Descriptor { get; }

        public IDictionary<string, int> FieldSizes { get; }

        public IDictionary<string, byte[]> FieldBytes { get; } =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public IDictionary<string, int> Variables { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    private enum TuningByteOrder
    {
        LittleEndian,
        BigEndian
    }
}

public sealed class TuningProtocolDescriptor
{
    [JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = "custom-tuning";

    public string ByteOrder { get; set; } = string.Empty;

    public Dictionary<string, JsonElement> Parameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public TuningDspMessageDescriptor DspMessage { get; set; } = new();

    public TuningTotaDescriptor Tota { get; set; } = new();

    public TuningUartSendDescriptor UartSend { get; set; } = new();
}

public sealed class TuningDspMessageDescriptor
{
    public List<TuningProtocolField> Layout { get; set; } = new();
}

public sealed class TuningTotaDescriptor
{
    public string HeaderTagHex { get; set; } = string.Empty;

    public List<TuningProtocolField> HeaderLayout { get; set; } = new();

    public TuningPacketFrameDescriptor PacketFrame { get; set; } = new();

    public List<JsonElement> InfoAreaFields { get; set; } = new();

    public JsonElement WaitTime { get; set; }

    public int PacketIndexBase { get; set; } = 0;

    public bool SendHeaderInfo { get; set; } = true;
}

public sealed class TuningPacketFrameDescriptor
{
    public JsonElement CommandType { get; set; }

    public JsonElement PacketLength { get; set; }

    public JsonElement PayloadLength { get; set; }

    public List<TuningProtocolField> Layout { get; set; } = new();
}

public sealed class TuningUartSendDescriptor
{
    public string Targets { get; set; } = "allOpenPorts";

    public JsonElement DelayBetweenPacketsMs { get; set; }
}

public sealed class TuningProtocolField
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string? Hex { get; set; }

    public JsonElement Value { get; set; }

    public string? ByteOrder { get; set; }

    public string? Algorithm { get; set; }

    public List<string> Inputs { get; set; } = new();
}

public sealed class TuningProtocolException : Exception
{
    public TuningProtocolException(string message)
        : base(message)
    {
    }

    public TuningProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record TuningPacketFrame(int Index, int Position, byte[] Data);

internal sealed record TuningPacketPlan(
    byte[] Header,
    byte[] InfoArea,
    IReadOnlyList<TuningPacketFrame> PacketFrames,
    int DelayBetweenPacketsMs);
