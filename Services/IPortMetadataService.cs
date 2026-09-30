using SerialPortTool.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 每端口备注 / 标签 / 分组的持久化。
/// </summary>
public interface IPortMetadataService
{
    /// <summary>
    /// 读取全部端口元数据。
    /// </summary>
    /// <returns>以串口名为键（大小写不敏感）的字典；没有记录或损坏时返回空字典。</returns>
    /// <remarks>
    /// Never throws and never returns null entries — this is loaded inside <c>ScanPortsAsync</c>, and a
    /// note about a port is not worth a failed scan. See the <c>SnippetService</c> precedent: a convenience
    /// feature's payload must not be able to prevent the app from being usable.
    /// </remarks>
    Task<IReadOnlyDictionary<string, PortMetadata>> LoadAsync();

    /// <summary>保存全部端口元数据。</summary>
    Task SaveAsync(IEnumerable<PortMetadata> metadata);

    /// <summary>把用户输入收敛成可持久化的一条记录（裁剪长度、归一化空白）。</summary>
    PortMetadata Sanitize(PortMetadata metadata);
}
