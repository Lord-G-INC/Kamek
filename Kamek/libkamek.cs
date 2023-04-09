using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Kamek; 
static class Library {
	public enum PatchType: byte {
		bin,
		xml,
		ini,
		dol,
	}

	public record struct PtrInfo(nint Ptr, int Size);

	static void Create_Patch(string[] patches, string gameID, PatchType patchType, uint baseAddress, 
		out byte[] bytes, out string str) {
		bytes = null;
		str = null;
		var modules = new List<Elf>();

		foreach (var patch in patches) {
            using var stream = new FileStream("/srv/http/smg/patches/" + patch + ".o", FileMode.Open, FileAccess.Read);
            modules.Add(new Elf(stream));
        }

		var externals = new Dictionary<string, uint>();

		var commentRegex = new Regex(@"^\s*#");
		var emptyLineRegex = new Regex(@"^\s*$");
		var assignmentRegex = new Regex(@"^\s*([a-zA-Z0-9_<>@,-\\$]+)\s*=\s*0x([a-fA-F0-9]+)\s*(#.*)?$");

		foreach (var line in File.ReadAllLines("/srv/http/smg/" + gameID + ".map")) {
			if (emptyLineRegex.IsMatch(line))
				continue;
			if (commentRegex.IsMatch(line))
				continue;

			var match = assignmentRegex.Match(line);
			if (match.Success)
				externals[match.Groups[1].Value] = uint.Parse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
			else
				Console.Error.WriteLine("unrecognised line in externals file: {0}", line);
		}

		// We need a default VersionList for the loop later
		VersionInfo versions = new();

		foreach (var version in versions.Mappers) {
			var linker = new Linker(version.Value);
			foreach (var module in modules)
				linker.AddModule(module);

			if (patchType == PatchType.bin)
				linker.LinkDynamic(externals);
			else
				linker.LinkStatic(baseAddress, externals);

			var kf = new KamekFile();
			kf.LoadFromLinker(linker);

			switch (patchType) {
				case PatchType.bin: {
					bytes = kf.Pack();
					// return kamekBin
					break;
				}
				case PatchType.xml: {
					str = kf.PackRiivolution();
					// return riivXml
					break;
				}
				case PatchType.ini: {
					str = kf.PackDolphin();
					// return dolphinIni
					break;
				}
				case PatchType.dol: {
					var dol = new Dol(new FileStream("/srv/http/smg/" + gameID + ".dol", FileMode.Open, FileAccess.Read));
					kf.InjectIntoDol(dol);
					bytes = dol.Write();
					// return dolBin
					break;
				}
			}
		}
	}

	[UnmanagedCallersOnly(EntryPoint = "create_patch")]
	public static unsafe PtrInfo Create_Patch(nint patches_ptr, int patches_size, nint gameID_ptr, 
		PatchType patchType, uint baseAddress) {
		string[] patches = new string[patches_size];
		char** ptr = (char**)patches_ptr;
		for (int i = 0; i < patches_size; i++)
			patches[i] = Marshal.PtrToStringAnsi((nint)ptr[i]);
		string gameID = Marshal.PtrToStringAnsi(gameID_ptr);
		Create_Patch(patches, gameID, patchType, baseAddress, out var bytes, out var str);
		PtrInfo info = new();
		if (bytes != null)
		{
			info.Ptr = Marshal.AllocHGlobal(bytes.Length);
			info.Size = bytes.Length;
			Marshal.Copy(bytes, 0, info.Ptr, bytes.Length);
		} else if (str != null)
		{
			info.Ptr = Marshal.StringToHGlobalAnsi(str);
			info.Size = str.Length;
		}
		return info;
	}
}
