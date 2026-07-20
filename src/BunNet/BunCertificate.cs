using System;
using System.IO;

#if NET
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
#endif

namespace BunNet
{
    /// <summary>
    /// Erzeugt selbstsignierte Zertifikate für HTTPS im Entwicklungsbetrieb —
    /// vollständig mit .NET-Bordmitteln, ohne OpenSSL-Aufruf.
    /// </summary>
    public static class BunCertificate
    {
        /// <summary>
        /// Stellt sicher, dass im Verzeichnis <paramref name="directory"/> ein
        /// selbstsigniertes Zertifikat (<c>cert.pem</c>) samt privatem Schlüssel
        /// (<c>key.pem</c>) liegt. Fehlende Dateien werden erzeugt, vorhandene
        /// bleiben unverändert. Die Pfade werden über die out-Parameter geliefert.
        /// </summary>
        /// <param name="directory">Zielordner, z. B. <c>cert</c>. Wird bei Bedarf angelegt.</param>
        /// <param name="hostName">Hostname für das Zertifikat, z. B. <c>localhost</c>.</param>
        /// <param name="certPath">Pfad zu <c>cert.pem</c>.</param>
        /// <param name="keyPath">Pfad zu <c>key.pem</c>.</param>
        public static void EnsureSelfSigned(string directory, string hostName, out string certPath, out string keyPath)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Verzeichnis fehlt.", nameof(directory));
            if (string.IsNullOrEmpty(hostName)) throw new ArgumentException("Hostname fehlt.", nameof(hostName));

            certPath = Path.Combine(directory, "cert.pem");
            keyPath = Path.Combine(directory, "key.pem");

            if (File.Exists(certPath) && File.Exists(keyPath)) return;

#if NET
            Directory.CreateDirectory(directory);

            using (RSA rsa = RSA.Create(2048))
            {
                CertificateRequest request = new CertificateRequest(
                    "CN=" + hostName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                // Zertifikat für localhost-Betrieb: Hostname + Loopback-Adressen.
                SubjectAlternativeNameBuilder alternativeNames = new SubjectAlternativeNameBuilder();
                alternativeNames.AddDnsName(hostName);
                alternativeNames.AddDnsName("localhost");
                alternativeNames.AddIpAddress(IPAddress.Loopback);
                alternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
                request.CertificateExtensions.Add(alternativeNames.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

                DateTimeOffset now = DateTimeOffset.UtcNow;
                using (X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1)))
                {
                    File.WriteAllText(certPath, certificate.ExportCertificatePem());
                    File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
                }
            }
#else
            throw new PlatformNotSupportedException(
                "Zertifikatserzeugung benötigt .NET 5 oder neuer. Unter netstandard2.1 bitte " +
                "vorhandene PEM-Dateien verwenden (z. B. mit openssl erzeugt).");
#endif
        }
    }
}
