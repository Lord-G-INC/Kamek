using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kamek {
	class CGI {
		public enum PatchType {
			bin,
			xml,
			ini,
			dol,
		}

		/*public enum Region {
			

		public static string[] regions = {
			"RMGE",
			"RMGP",
			"RMGK",
			"SB4J",
			"SB4E",
			"SB4P",
			"SB4W",
			"SB4K",
		};*/

		static void ShowError(string msg) {
			Console.WriteLine("Status: 400 Bad Request");
			Console.WriteLine("Content-Length: {0}", msg.Length + 1);
			Console.WriteLine("Content-Type: text/plain; charset=UTF-8");
			Console.WriteLine();
			Console.WriteLine(msg);
			Environment.Exit(400);
		}

		static void Main(string[] args) {
			string queryString = Environment.GetEnvironmentVariable("QUERY_STRING");
			if (String.IsNullOrEmpty(queryString))
				ShowError("Nothing to do");

			var modules = new List<Elf>();
			uint baseAddress = 0x80001900;
			PatchType patchType = PatchType.xml;
			string game = null;
			char region = '0';

			string[] flags = queryString.Split('&');
			foreach (var flag in flags) {
				if (flag.StartsWith("type")) {
					string patchTypeStr = flag.Substring(5);
					try {
						patchType = (PatchType)Enum.Parse(typeof(PatchType), patchTypeStr);
					} catch (ArgumentException) {
						ShowError(patchTypeStr + ": invalid patch type");
					}
				} else if (flag.StartsWith("patch")) {
					string patch = flag.Substring(6);
					if (patch == "LoadSpecifiedGalaxyOnFileSelected")
						continue;
					//string patchPath = patch + ".o";
					string patchPath = "/srv/http/smg/patches/" + patch + ".o";
					if (!File.Exists(patchPath))
						ShowError(patch + ": no such patch");

					using (var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read)) {
						modules.Add(new Elf(stream));
					}
				} else if (flag.StartsWith("address")) {
					string baseAddressStr = flag.Substring(8);
					try {
						baseAddress = uint.Parse(baseAddressStr, System.Globalization.NumberStyles.HexNumber);
					} catch (FormatException) {
						ShowError(baseAddressStr + ": invalid address");
					}
				} else if (flag.StartsWith("game")) {
					game = flag.Substring(5);
					if (game != "RMG" && game != "SB4")
							ShowError(game + ": invalid game");
				} else if (flag.StartsWith("region")) {
					string regionStr = flag.Substring(7);
					if (regionStr != "J" && regionStr != "E" && regionStr != "P" && regionStr != "W" && regionStr != "K")
						ShowError(flag + ": invalid region");
					region = regionStr.Substring(0, 1)[0];
				} else if (flag.StartsWith("LoadSpecifiedGalaxyOnFileSelected")) {
					string galaxyName = flag.Substring(34);

					byte[] buffer;
					using (var fileStream = new FileStream("/srv/http/smg/patches/galaxyname.o", FileMode.Open, FileAccess.Read)) {
						buffer = new byte[fileStream.Length];
						fileStream.Read(buffer, 0, buffer.Length);
					}

					var galaxyNameBytes = Encoding.UTF8.GetBytes(galaxyName);
					Buffer.BlockCopy(galaxyNameBytes, 0, buffer, 52, Math.Min(galaxyNameBytes.Length, 29));

					using (var memoryStream = new MemoryStream(buffer)) {
						modules.Add(new Elf(memoryStream));
					}
				}/* else {
					ShowError(flag + ": invalid flag");
				}*/
			}

			if (game == null)
				ShowError("No game specified");
			if (region == '0')
				ShowError("No region specified");
			if (modules.Count == 0)
				ShowError("No patches specified");

			string gameID = game + region + "01";

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
						int kamekBinLength = kamekBin.Length;
						Console.WriteLine("Content-Length: {0}", kamekBinLength);
						Console.WriteLine("Content-Type: application/vnd.kamek");
						Console.WriteLine("Content-Disposition: attachment; filename=\"CustomCode.bin\"");
						Console.WriteLine();
						Stream stdout = Console.OpenStandardOutput();
						stdout.Write(kamekBin, 0, kamekBinLength);
						break;
					}
					case PatchType.xml: {
						string riivXml = kf.PackRiivolution();
						Console.WriteLine("Content-Length: {0}", riivXml.Length + 1);
						Console.WriteLine("Content-Type: text/xml");
						Console.WriteLine("Content-Disposition: attachment; filename=\"{0}.xml\"", gameID);
						Console.WriteLine();
						Console.WriteLine(riivXml);
						break;
					}
					case PatchType.ini: {
						string dolphinIni = kf.PackDolphin();
						Console.WriteLine("Content-Length: {0}", dolphinIni.Length + 1);
						Console.WriteLine("Content-Type: text/plain");
						Console.WriteLine("Content-Disposition: attachment; filename=\"{0}.ini\"", gameID);
						Console.WriteLine();
						Console.WriteLine(dolphinIni);
						break;
					}
					case PatchType.dol: {
						var dol = new Dol(new FileStream("/srv/http/smg/" + gameID + ".dol", FileMode.Open, FileAccess.Read));
						kf.InjectIntoDol(dol);
						byte[] dolBin = dol.Write();
						Console.WriteLine("Content-Length: {0}", dolBin.Length);
						Console.WriteLine("Content-Type: application/vnd.dol");
						Console.WriteLine("Content-Disposition: attachment; filename=\"{0}.dol\"", gameID);
						Console.WriteLine();
						Stream stdout = Console.OpenStandardOutput();
						stdout.Write(dolBin);
						break;
					}
				}
			}
		}
	}
}
