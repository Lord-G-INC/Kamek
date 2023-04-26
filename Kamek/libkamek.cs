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

	public enum Game {
		SMG1,
		SMG2,
	}

	public enum Region {
		RMGJ,
		RMGE,
		RMGP,
		RMGK,
		SB4J,
		SB4E,
		SB4P,
		SB4K,
		SB4W,
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

	public static Dictionary<Region, Dictionary<string, uint>> externals = new Dictionary<Region, Dictionary<string, uint>>();
	public static Dictionary<Game, Dictionary<string, Patch>> patches = new Dictionary<Game, Dictionary<string, Patch>>();

	[UnmanagedCallersOnly(EntryPoint = "kamek_init")]
	static void Init() {
		foreach (Game game in Enum.GetValues<Game>()) {
			Dictionary<string, Patch> gamePatches = new Dictionary<string, Patch>();
			JArray jsonArr = JArray.Parse(File.ReadAllText(game == Game.SMG2 ? "/srv/http/smg/smg2-patches.json" : "/srv/http/smg/smg-patches.json"));

			foreach (JObject jsonObj in jsonArr.Cast<JObject>()) {
				string patchName = jsonObj.GetValue("name").ToString();

				Patch patch = new Patch();
				using (var stream = new FileStream((game == Game.SMG2 ? "/srv/http/smg/smg2-patches/" : "/srv/http/smg/smg-patches/") + patchName + ".o", FileMode.Open, FileAccess.Read)) {
					patch.Code = new Elf(stream);
				}

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

				gamePatches[patchName] = patch;
			}

			patches[game] = gamePatches;
		}

		var commentRegex = new Regex(@"^\s*#");
		var emptyLineRegex = new Regex(@"^\s*$");
		var assignmentRegex = new Regex(@"^\s*([a-zA-Z0-9_<>@,-\\$]+)\s*=\s*0x([a-fA-F0-9]+)\s*(#.*)?$");

		foreach (Region region in Enum.GetValues<Region>()) {
			Dictionary<string, uint> gameExternals = new Dictionary<string, uint>();
			string externalsPath = "/srv/http/smg/" + region.ToString() + "01.map";
			foreach (var line in File.ReadAllLines(externalsPath)) {
				if (emptyLineRegex.IsMatch(line))
					continue;
				if (commentRegex.IsMatch(line))
					continue;

				var match = assignmentRegex.Match(line);
				if (match.Success)
					gameExternals[match.Groups[1].Value] = uint.Parse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
				else
					Console.Error.WriteLine("unrecognised line in {0}: {1}", externalsPath, line);
			}

			externals[region] = gameExternals;
		}
	}

	[UnmanagedCallersOnly(EntryPoint = "kamek_createpatch")]
	public static unsafe PtrInfo CreatePatch(nint pPatches, int patchesCount, nint pPatchArgs, Region gameID,
		PatchType patchType, uint baseAddress) {
		Game game;
		int gameIDValue = (int)gameID;
		if (gameIDValue >= 0 && gameIDValue < 4)
			game = Game.SMG1;
		else if (gameIDValue >= 4 && gameIDValue < 9)
			game = Game.SMG2;
		else
			throw new InvalidOperationException("Invalid game");

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
				Patch patchToAdd = patches[game][Marshal.PtrToStringAnsi((nint)ppPatches[i])];
				for (int j = 0; j < patchToAdd.Arguments.Count; ++j, ++argIndex) {
					PatchArg patchArg = patchToAdd.Arguments[j];
					if (patchArg.IntOffsets == null) {
						int argLen = 0;
						while (Marshal.ReadByte((nint)ppPatchArgs[argIndex], argLen) != 0)
							argLen++;
						argLen++;
						patchArg.StrVal = new byte[argLen];
						Marshal.Copy((nint)ppPatchArgs[argIndex], patchArg.StrVal, 0, argLen);
					} else {
						patchArg.IntVal = (int)ppPatchArgs[argIndex];
					}

					patchToAdd.Arguments[j] = patchArg;
				}

				linker.AddModule(patchToAdd);
			}

			if (patchType == PatchType.bin)
				linker.LinkDynamic(externals[gameID]);
			else
				linker.LinkStatic(baseAddress, externals[gameID]);

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
					var dol = new Dol(new FileStream("/srv/http/smg/" +  gameID.ToString() + "01.dol", FileMode.Open, FileAccess.Read));
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
