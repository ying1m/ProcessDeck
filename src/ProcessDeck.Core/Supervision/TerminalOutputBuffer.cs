using System.Text;

namespace ProcessDeck.Core.Supervision;

/// <summary>
/// 终端输出环形缓冲。
///
/// 它把裸字节流（含 VT 转义序列）整理成「人能看的文本行」。这里必须模拟终端的行为，
/// 而不是简单按字符过滤 —— 三件脏活：
///
///   1. <b>UTF-8 跨块截断</b>：多字节字符可能被 read buffer 切成两半，
///      必须用有状态解码器，否则中文会变成乱码。
///
///   2. <b>光标模型</b>：<c>\r</c> 只是把光标移回行首，<b>不会擦除已打印的内容</b>。
///      这一点极易搞错：如果把 <c>\r</c> 当成「清空当前行」，
///      那么最常见的行结束序列 <c>\r\n</c> 就会变成「清空 → 提交一个空行」，
///      整份日志的内容全部丢失（只留下一堆空行）。
///      正确做法是维护一个字符缓冲 + 光标列，写入时按列覆盖。
///      这样进度条（<c>\rprogress 50%</c> 原地覆写）和普通换行都能正确还原。
///
///   3. <b>转义序列</b>：颜色、光标移动、清屏等 CSI/OSC 序列不能出现在文本里。
/// </summary>
public sealed class TerminalOutputBuffer
{
    private const int DefaultMaxLines = 800;
    private const int MaxLineLength = 4000;
    private const int TabWidth = 8;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _currentLine = new();
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly int _maxLines;

    private char[] _charBuffer = new char[4096];
    private EscapeState _escapeState = EscapeState.None;

    /// <summary>光标所在的列（0 起）。写入从这一列开始覆盖。</summary>
    private int _cursor;

    public TerminalOutputBuffer(int maxLines = DefaultMaxLines)
    {
        _maxLines = maxLines;
    }

    /// <summary>最近一次写入的时间，用于「等输出安静下来」这类同步判断。</summary>
    public DateTime LastWriteUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>输出流是否已结束（进程退出或控制台关闭）。</summary>
    public bool IsClosed { get; private set; }

    /// <summary>内容变化通知（可能在泵线程上触发）。</summary>
    public event Action? Changed;

    /// <summary>追加原始字节。</summary>
    public void Append(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var charCount = _decoder.GetCharCount(buffer, 0, count, flush: false);
        if (charCount == 0)
        {
            return;
        }

        if (_charBuffer.Length < charCount)
        {
            _charBuffer = new char[Math.Max(charCount, _charBuffer.Length * 2)];
        }

        var decoded = _decoder.GetChars(buffer, 0, count, _charBuffer, 0, flush: false);
        AppendChars(_charBuffer, decoded);
    }

    /// <summary>追加已解码文本。</summary>
    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AppendChars(text.ToCharArray(), text.Length);
    }

    /// <summary>标记输出流结束，并把残留的半行提交出去。</summary>
    public void MarkClosed()
    {
        lock (_gate)
        {
            IsClosed = true;
            FlushCurrentLineLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>取当前全部行的快照（最新的在最后）。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<string>(_lines.Count + 1);
            result.AddRange(_lines);

            if (_currentLine.Length > 0)
            {
                result.Add(_currentLine.ToString());
            }

            return result;
        }
    }

    /// <summary>等待输出安静指定的时长，或直到超时。</summary>
    public async Task<bool> WaitForQuietAsync(TimeSpan quiet, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var since = DateTime.UtcNow - LastWriteUtc;
            if (since >= quiet)
            {
                return true;
            }

            var remaining = quiet - since;
            var wait = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private void AppendChars(char[] chars, int count)
    {
        var changed = false;

        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var c = chars[i];

                if (_escapeState != EscapeState.None)
                {
                    ConsumeEscape(c);
                    changed = true;
                    continue;
                }

                switch (c)
                {
                    case '\x1b':
                        _escapeState = EscapeState.Escape;
                        changed = true;
                        break;

                    case '\n':
                        // 换行：提交当前行。注意光标归零由 \r 负责，这里不擦除内容。
                        FlushCurrentLineLocked();
                        changed = true;
                        break;

                    case '\r':
                        // 回车：只把光标移回行首，已经打印的内容必须保留。
                        _cursor = 0;
                        changed = true;
                        break;

                    case '\b':
                        // 退格：光标左移一格，不删除字符（终端语义）。
                        if (_cursor > 0)
                        {
                            _cursor--;
                        }

                        changed = true;
                        break;

                    case '\t':
                        AdvanceToNextTabStop();
                        changed = true;
                        break;

                    case '\0':
                        break;

                    default:
                        WriteAtCursor(c);
                        changed = true;

                        // 某些程序（比如 npm 的进度输出）可能长时间不换行，
                        // 必须强制断开，否则内存会被一行吃满。
                        if (_currentLine.Length >= MaxLineLength && _cursor >= MaxLineLength)
                        {
                            FlushCurrentLineLocked();
                        }

                        break;
                }
            }

            if (changed)
            {
                LastWriteUtc = DateTime.UtcNow;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>在光标处覆盖写入一个字符，并把光标右移。</summary>
    private void WriteAtCursor(char c)
    {
        if (_cursor < _currentLine.Length)
        {
            _currentLine[_cursor] = c;
        }
        else
        {
            // 光标跳到了已有内容之后（例如被 \r 后再写入更长的内容），用空格补齐。
            while (_currentLine.Length < _cursor)
            {
                _currentLine.Append(' ');
            }

            _currentLine.Append(c);
        }

        _cursor++;
    }

    private void AdvanceToNextTabStop()
    {
        var next = ((_cursor / TabWidth) + 1) * TabWidth;

        while (_cursor < next)
        {
            WriteAtCursor(' ');
        }
    }

    /// <summary>
    /// 跳过 VT 转义序列。
    /// 支持 CSI（ESC [ ... 终止字节）、OSC（ESC ] ... BEL 或 ESC \）以及普通双字符转义。
    /// </summary>
    private void ConsumeEscape(char c)
    {
        switch (_escapeState)
        {
            case EscapeState.Escape:
                _escapeState = c switch
                {
                    '[' => EscapeState.Csi,
                    ']' => EscapeState.Osc,
                    _ => EscapeState.None,
                };
                break;

            case EscapeState.Csi:
                // CSI 的参数字节是 0x30–0x3F、中间字节 0x20–0x2F，终止字节是 0x40–0x7E。
                if (c >= '\x40' && c <= '\x7E')
                {
                    _escapeState = EscapeState.None;
                }

                break;

            case EscapeState.Osc:
                if (c == '\x07')
                {
                    _escapeState = EscapeState.None;
                }
                else if (c == '\x1b')
                {
                    _escapeState = EscapeState.OscTerminator;
                }

                break;

            case EscapeState.OscTerminator:
                _escapeState = EscapeState.None;
                break;
        }
    }

    private void FlushCurrentLineLocked()
    {
        var line = _currentLine.ToString().TrimEnd();
        _currentLine.Clear();
        _cursor = 0;

        _lines.Enqueue(line);

        while (_lines.Count > _maxLines)
        {
            _lines.Dequeue();
        }
    }

    private enum EscapeState
    {
        None,
        Escape,
        Csi,
        Osc,
        OscTerminator,
    }
}
