using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// プレイヤーが付けた都市名と、都市ごとに独立した保存ファイルを管理します。
/// </summary>
public sealed class CitySaveStore
{
    private readonly string _directory;
    private readonly string[] _legacyPaths;

    /// <summary>
    /// 保存場所と移行可能な旧保存を設定します。
    /// 引数: directory（string）保存フォルダー、legacyPaths（string[]）旧保存の絶対パス。
    /// 返り値: なし（コンストラクター）。
    /// </summary>
    public CitySaveStore(string directory, params string[] legacyPaths)
    {
        _directory = Path.GetFullPath(directory); _legacyPaths = legacyPaths;
    }

    /// <summary>
    /// 都市名をファイル名へ転用せず、内部識別子から保存先を取得します。
    /// 引数: id（string）都市保存の識別子。返り値: string 絶対パス。
    /// </summary>
    public string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("都市保存の識別子が不正です。");
        return Path.Combine(_directory, id + ".json");
    }

    /// <summary>
    /// 都市を原子的に保存し、上書き前の内容を別ファイルへ保持します。
    /// 引数: state（CityState）都市、id（string）保存識別子。返り値: void。
    /// </summary>
    public void Save(CityState state, string id)
    {
        var path = PathFor(id); Directory.CreateDirectory(_directory);
        File.WriteAllText(path + ".tmp", CityModel.Serialize(state), new UTF8Encoding(false));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>
    /// 都市保存と旧保存の一覧を都市名・更新日時とともに列挙します。
    /// 引数: なし。返り値: IReadOnlyList&lt;CitySaveEntry&gt; 保存一覧。
    /// </summary>
    public IReadOnlyList<CitySaveEntry> List()
    {
        var result = new List<CitySaveEntry>();
        string[] paths = Array.Empty<string>();
        try { if (Directory.Exists(_directory)) paths = Directory.GetFiles(_directory, "*.json"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var path in paths.Concat(_legacyPaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > CityModel.MaxPayloadBytes) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                var name = "名前未設定の都市";
                if (json.RootElement.TryGetProperty("CityName", out var value))
                {
                    if (value.ValueKind != JsonValueKind.String || !CityModel.ValidateCityName(value.GetString(), out _)) continue;
                    name = value.GetString()!;
                }
                result.Add(new CitySaveEntry(path, name, File.GetLastWriteTimeUtc(path)));
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(entry => entry.UpdatedUtc).ToList();
    }

    /// <summary>
    /// 一覧に存在する都市だけを読み込み対象にし、任意ファイルの指定を防ぎます。
    /// 引数: path（string）保存先。返り値: string 保存済みJSON。
    /// </summary>
    public string Read(string path)
    {
        if (!List().Any(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))) throw new IOException("保存一覧に都市が見つかりません。");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// 現行の保存フォルダー内にある都市の識別子を取得します。
    /// 引数: path（string）保存先。返り値: string? 現行識別子。旧保存はnull。
    /// </summary>
    public string? IdFor(string path) => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) ? Path.GetFileNameWithoutExtension(path) : null;
}

/// <summary>
/// 保存画面に表示する都市名と更新日時です。
/// </summary>
public sealed record CitySaveEntry(string Path, string Name, DateTime UpdatedUtc);
