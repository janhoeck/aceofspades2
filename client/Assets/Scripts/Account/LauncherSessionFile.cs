#if UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AoS2.Contracts;
using Newtonsoft.Json;
using UnityEngine;

namespace AoS2.Client.Account
{
    /// <summary>
    /// Nur im Unity-Editor unter Windows: liest die Anmeldung, die der Launcher in
    /// <c>%LocalAppData%\AceOfSpades2\session.dat</c> gespeichert hat. Die Datei ist mit Windows DPAPI
    /// verschlüsselt; entschlüsseln kann sie nur derselbe Windows-Benutzer, also auch der Editor.
    /// </summary>
    internal static class LauncherSessionFile
    {
        public static SavedSession TryRead()
        {
            try
            {
                var dataDir = Environment.GetEnvironmentVariable(SavedSession.DataDirVariable);
                if (string.IsNullOrWhiteSpace(dataDir))
                    dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SavedSession.DataFolderName);

                var path = Path.Combine(dataDir, SavedSession.FileName);
                if (!File.Exists(path))
                    return null;

                var json = Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(SavedSession.Entropy));
                return json == null ? null : JsonConvert.DeserializeObject<SavedSession>(Encoding.UTF8.GetString(json));
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"Gespeicherte Anmeldung des Launchers nicht lesbar: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- Windows DPAPI (crypt32.dll)

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        private const int UiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = new DataBlob();
            try
            {
                var input = new DataBlob { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
                var entropyBlob = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output))
                    return null;

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
                dataHandle.Free();
                entropyHandle.Free();
            }
        }
    }
}
#endif
