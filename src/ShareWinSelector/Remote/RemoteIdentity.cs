using System.IO;
using System.Security.Cryptography;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携でこの PC を名乗るための鍵（ECDSA P-256）。初めて使うときに作って設定フォルダに置く。
/// 第三者の保証は使わず、ペアリングで相手の公開鍵を覚えておく（ピン留め）ことで、なりすましを防ぐ。
/// 鍵は Windows の鍵の保管庫には置かず、メモリと設定フォルダだけで扱う（落ちても保管庫に何も残らない）。
/// ファイルは Windows のユーザーごとの保護（DPAPI）で包むので、ほかのユーザーやほかの PC にコピーしても使えない。
/// 「設定を削除して終了」で設定フォルダごと消える。
/// </summary>
internal sealed class RemoteIdentity : IDisposable
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "remote-identity.key");

    /// <summary>名乗るための署名鍵。</summary>
    public ECDsa Key { get; }

    /// <summary>公開鍵（SubjectPublicKeyInfo 形式）。相手に渡し、覚えてもらう値の元。</summary>
    public byte[] PublicKey { get; }

    /// <summary>公開鍵の指紋（SHA-256、16 進）。ペアリングで覚える値。</summary>
    public string Thumbprint { get; }

    private RemoteIdentity(ECDsa key)
    {
        Key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Thumbprint = ThumbprintOf(PublicKey);
    }

    /// <summary>設定フォルダの鍵を読む。無ければ作って保存する。</summary>
    public static RemoteIdentity LoadOrCreate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (File.Exists(FilePath))
            {
                var data = File.ReadAllBytes(FilePath);
                if (data.AsSpan().StartsWith(ProtectedMagic))
                {
                    var plain = ProtectedData.Unprotect(data[ProtectedMagic.Length..], Entropy, DataProtectionScope.CurrentUser);
                    key.ImportPkcs8PrivateKey(plain, out _);
                    CryptographicOperations.ZeroMemory(plain);
                }
                else
                {
                    // 保護する前の版で作った鍵: 読んで、保護して書き直す
                    key.ImportPkcs8PrivateKey(data, out _);
                    Save(key);
                }
            }
            else
            {
                Directory.CreateDirectory(AppSettings.Folder);
                Save(key);
                Log.Info("リモート連携: この PC の鍵を作りました");
            }
            return new RemoteIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    // 保護したファイルの頭に付ける印と、保護に混ぜる値（このアプリの鍵だと分かるように）
    private static ReadOnlySpan<byte> ProtectedMagic => "SWSK1"u8;
    private static readonly byte[] Entropy = "ShareWinSelector remote identity"u8.ToArray();

    private static void Save(ECDsa key)
    {
        var plain = key.ExportPkcs8PrivateKey();
        var data = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        // 書きかけで落ちても壊れないよう、別のファイルに書いてから置き換える
        string temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, [.. ProtectedMagic, .. data]);
        File.Move(temp, FilePath, overwrite: true);
    }

    public static string ThumbprintOf(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey));

    public void Dispose() => Key.Dispose();
}
