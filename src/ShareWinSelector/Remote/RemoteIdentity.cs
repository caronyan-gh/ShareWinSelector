using System.IO;
using System.Security.Cryptography;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携でこの PC を名乗るための鍵（ECDSA P-256）。初めて使うときに作って設定フォルダに置く。
/// 第三者の保証は使わず、ペアリングで相手の公開鍵を覚えておく（ピン留め）ことで、なりすましを防ぐ。
/// 鍵は Windows の鍵の保管庫には置かず、メモリと設定フォルダだけで扱う（落ちても保管庫に何も残らない）。
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
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(FilePath), out _);
            }
            else
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllBytes(FilePath, key.ExportPkcs8PrivateKey());
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

    public static string ThumbprintOf(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey));

    /// <summary>
    /// ペアリングの番号（4 桁）。親と子の公開鍵から作るので、両方の画面に同じ番号が出ていれば、間に誰も割り込んでいない。
    /// </summary>
    public static string PairCode(byte[] parentPublicKey, byte[] childPublicKey)
    {
        var hash = SHA256.HashData([.. parentPublicKey, .. childPublicKey]);
        uint value = BitConverter.ToUInt32(hash, 0);
        return (value % 10000).ToString("D4");
    }

    public void Dispose() => Key.Dispose();
}
