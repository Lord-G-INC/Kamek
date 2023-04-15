using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kamek; 
static class Library {
	public enum PatchType: byte {
		bin,
		xml,
		ini,
		dol,
	}

	public enum ArgumentType {
		None,
		String,
		Int,
	}

	public enum Games {
		RMGJ,
		RMGE,
		RMGP,
		RMGK,
		SB4J,
		SB4E,
		SB4P,
		SB4W,
		SB4K,
	}

	public struct PatchArg {
		public string Name;
		public byte[] StrVal;
		public int IntVal;
		public List<int> IntOffsets;
	}

	public struct Patch {
		public Elf Code;
		public List<PatchArg> Arguments;
	}

	public record struct PtrInfo(nint Ptr, int Size);

	public static Dictionary<string, uint> externals = new Dictionary<string, uint>();
	public static Dictionary<string, Patch> patches = new Dictionary<string, Patch>();

	[UnmanagedCallersOnly(EntryPoint = "kamek_init")]
	static void Init() {
		JArray jsonArr = JArray.Parse(File.ReadAllText("/srv/http/smg/smg2-patches.json"));

		foreach (JObject jsonObj in jsonArr.Cast<JObject>()) {
			string patchName = jsonObj.GetValue("name").ToString();

			Elf patchElf;
			using (var stream = new FileStream("/srv/http/smg/patches/" + patchName + ".o", FileMode.Open, FileAccess.Read)) {
				patchElf = new Elf(stream);
			}
			Patch patch = new Patch {
				Code = patchElf
			};

			patch.Arguments = new List<PatchArg>();
			if (jsonObj.TryGetValue("arguments", out JToken argumentsObj)) {
				JArray argumentsArray = (JArray)argumentsObj;
				foreach (JObject argument in argumentsArray.Cast<JObject>()) {
					PatchArg patchArg = new PatchArg { Name = argument.GetValue("name").ToString() };
					if (argument.TryGetValue("offsets", out JToken offsets))
						patchArg.IntOffsets = offsets.Select(j => (int)j).ToList();

					patch.Arguments.Add(patchArg);
				}
			}

			patches[patchName] = patch;
		}

		var commentRegex = new Regex(@"^\s*#");
		var emptyLineRegex = new Regex(@"^\s*$");
		var assignmentRegex = new Regex(@"^\s*([a-zA-Z0-9_<>@,-\\$]+)\s*=\s*0x([a-fA-F0-9]+)\s*(#.*)?$");

		foreach (var line in File.ReadAllLines("/srv/http/smg/SB4E01.map")) {
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
	}

	[UnmanagedCallersOnly(EntryPoint = "kamek_createpatch")]
	public static unsafe PtrInfo CreatePatch(nint pPatches, int patchesCount, nint pPatchArgs, nint pGameID,
		PatchType patchType, uint baseAddress) {
		string[] strs = null;
		byte[] bytes = null;
		char** ppPatches = (char**)pPatches;
		char** ppPatchArgs = (char **)pPatchArgs;
		uint argIndex = 0;

		// We need a default VersionList for the loop later
		VersionInfo versions = new();

		foreach (var version in versions.Mappers) {
			var linker = new Linker(version.Value);
			for (int i = 0; i < patchesCount; i++) {
				Patch patchToAdd = patches[Marshal.PtrToStringAnsi((nint)ppPatches[i])];
				for (int j = 0; j < patchToAdd.Arguments.Count; ++j, ++argIndex) {
					PatchArg patchArg = patchToAdd.Arguments[j];
					if (patchArg.IntOffsets == null) {
						int argLen = 0;
						while (Marshal.ReadByte((nint)ppPatchArgs[j], argLen) != 0)
							argLen++;
						argLen++;
						patchArg.StrVal = new byte[argLen];
						Marshal.Copy((nint)ppPatchArgs[j], patchArg.StrVal, 0, argLen);
					} else {
						patchArg.IntVal = Marshal.ReadInt32((nint)ppPatchArgs[j]);
					}

					patchToAdd.Arguments[j] = patchArg;
				}

				linker.AddModule(patchToAdd);
			}

			if (patchType == PatchType.bin)
				linker.LinkDynamic(externals);
			else
				linker.LinkStatic(baseAddress, externals);

			var kf = new KamekFile();
			kf.LoadFromLinker(linker);

			switch (patchType) {
				case PatchType.bin: {
					bytes = kf.Pack();
					break;
				}
				case PatchType.xml: {
					strs = kf.PackRiivolution();
					break;
				}
				case PatchType.ini: {
					strs = kf.PackDolphin();
					break;
				}
				case PatchType.dol: {
					var dol = new Dol(new FileStream("/srv/http/smg/" +  Marshal.PtrToStringAnsi(pGameID) + ".dol", FileMode.Open, FileAccess.Read));
					kf.InjectIntoDol(dol);
					bytes = dol.Write();
					break;
				}
			}
		}

		PtrInfo info = new();
		if (bytes != null) {
			info.Size = bytes.Length;
			info.Ptr = Marshal.AllocHGlobal(bytes.Length);
			Marshal.Copy(bytes, 0, info.Ptr, bytes.Length);
		} else if (strs != null) {
			info.Size = strs.Length;
			info.Ptr = Marshal.AllocHGlobal(strs.Length * 8);
			byte** sptrs = (byte**)info.Ptr;
			for (int i = 0; i < strs.Length; i++)
				sptrs[i] = (byte*)Marshal.StringToHGlobalAnsi(strs[i]);
		}

		return info;
	}
}
