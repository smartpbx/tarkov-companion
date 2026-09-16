using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Platform.Windows.Devices;

namespace TarkovCompanion.WindowsSmokeTests;

public sealed class WindowsDpapiDesktopIdentitySignerTests
{
    [Fact]
    public async Task ProtectedIdentitySurvivesRestartAndSignsProtocolTranscriptInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("tarkov-companion-identity-");
        try
        {
            var path = Path.Combine(directory.FullName, "desktop-identity.dpapi");
            DesktopIdentityKey originalPublicKey;
            var transcriptHash = SHA256.HashData("paired-desktop-identity-test"u8);
            using (var created = await WindowsDpapiDesktopIdentitySigner.OpenOrCreateAsync(path))
            {
                originalPublicKey = created.PublicKey;
                var signature = created.Sign(PairingCryptography.EncodeDesktopSignatureInput(transcriptHash));
                Assert.True(PairingCryptography.VerifyDesktopSignature(created.PublicKey, transcriptHash, signature));
            }

            using var reopened = await WindowsDpapiDesktopIdentitySigner.OpenOrCreateAsync(path);
            Assert.Equal(originalPublicKey, reopened.PublicKey);
            var persisted = await File.ReadAllBytesAsync(path);
            Assert.NotEmpty(persisted);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CorruptProtectedIdentityFailsClosedInsteadOfReplacingPinnedKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("tarkov-companion-identity-corrupt-");
        try
        {
            var path = Path.Combine(directory.FullName, "desktop-identity.dpapi");
            await File.WriteAllBytesAsync(path, "not-dpapi"u8.ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await WindowsDpapiDesktopIdentitySigner.OpenOrCreateAsync(path));

            Assert.Equal("not-dpapi"u8.ToArray(), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
