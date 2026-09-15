using System.IO;
using System.Text.Json;

namespace Ccin.Core;

/// <summary>ウィンドウの位置と大きさ。物理ピクセル。</summary>
internal sealed record WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// ウィンドウの位置と大きさの記憶。
///
/// 表示名と番号は「再起動後に同じ Claude Code を判別する手段が無い」ため永続化しないと
/// 決めたが、窓の位置にその問題はない。窓は 1 つで、画面座標に紐づくだけ。
///
/// 仮想デスクトップは影響しない。デスクトップが変わっても画面の座標系は同じで、
/// どの机でも同じ位置に出るのはむしろ望ましい。効いてくるのはモニタ構成の変化で、
/// そちらは復元側で画面内に掛かるかを確かめる。
/// </summary>
internal static class WindowPlacement
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "window.json");

    public static WindowBounds? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(Path));
        }
        catch
        {
            // 壊れていたら既定の位置で開く。位置の記憶で起動を止めない
            return null;
        }
    }

    public static void Save(WindowBounds bounds)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(bounds));
        }
        catch
        {
            // 保存できなくても実害は「次回に覚えていない」だけ
        }
    }
}
