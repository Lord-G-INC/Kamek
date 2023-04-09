using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kamek {
	class library {
		public enum PatchType {
			bin,
			xml,
			ini,
			dol,
		}

		static void create_patch(string[] patches, string gameID, PatchType patchType, uint baseAddress) {
			var modules = new List<Elf>();

			foreach (var patch in patches) {
				using (var stream = new FileStream("/srv/http/smg/patches/" + patch + ".o", FileMode.Open, FileAccess.Read)) {
					modules.Add(new Elf(stream));
				}
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
			VersionInfo versions = new VersionInfo();

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
						byte[] kamekBin = kf.Pack();
						// return kamekBin
						break;
					}
					case PatchType.xml: {
						string riivXml = kf.PackRiivolution();
						// return riivXml
						break;
					}
					case PatchType.ini: {
						string dolphinIni = kf.PackDolphin();
						// return dolphinIni
						break;
					}
					case PatchType.dol: {
						var dol = new Dol(new FileStream("/srv/http/smg/" + gameID + ".dol", FileMode.Open, FileAccess.Read));
						kf.InjectIntoDol(dol);
						byte[] dolBin = dol.Write();
						// return dolBin
						break;
					}
				}
			}
		}
	}
}
