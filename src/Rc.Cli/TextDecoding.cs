using System.Text;

namespace Rc.Cli;

/// <summary>
/// 对端输出解码：优先严格 UTF-8；失败时回退到系统 ANSI 代码页（如 GBK）。
/// .NET Core 的 Encoding.Default 恒为 UTF-8，必须经 CodePagesEncodingProvider
/// 用 Encoding.GetEncoding(0) 取系统 ANSI 代码页。
/// </summary>
internal static class TextDecoding
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextDecoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(0).GetString(data);
        }
    }
}
