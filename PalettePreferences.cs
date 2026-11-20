using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

internal static class PalettePreferences
{
    private static readonly string PathToPreset = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rival 3 RGB preset.txt");
    private static readonly string PresetDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets");

    internal static bool Load(Color[] colors, bool[] off)
    {
        return LoadFrom(PathToPreset, colors, off);
    }

    internal static bool LoadNumbered(int number, Color[] colors, bool[] off)
    {
        return LoadFrom(NumberedPath(number), colors, off);
    }

    internal static int[] GetPresetNumbers()
    {
        if (!Directory.Exists(PresetDirectory))
            return new int[0];

        return Directory.EnumerateFiles(PresetDirectory, "*.txt")
            .Where(path => string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name =>
            {
                int number;
                return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                    number > 0 && name == number.ToString(CultureInfo.InvariantCulture) ? number : 0;
            })
            .Where(number => number > 0).OrderBy(number => number).ToArray();
    }

    internal static int SaveNew(Color[] colors, bool[] off)
    {
        Directory.CreateDirectory(PresetDirectory);
        int[] numbers = GetPresetNumbers();
        int number = numbers.Length == 0 ? 1 : checked(numbers[numbers.Length - 1] + 1);
        while (true)
        {
            string path = NumberedPath(number);
            try
            {
                SaveTo(path, colors, off, false);
                return number;
            }
            catch (IOException)
            {
                if (!File.Exists(path))
                    throw;
                number = checked(number + 1);
            }
        }
    }

    private static string NumberedPath(int number)
    {
        return Path.Combine(PresetDirectory, number.ToString(CultureInfo.InvariantCulture) + ".txt");
    }

    private static bool LoadFrom(string path, Color[] colors, bool[] off)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != colors.Length)
                return false;

            var loadedColors = new Color[colors.Length];
            var loadedOff = new bool[off.Length];
            for (int zone = 0; zone < lines.Length; zone++)
            {
                string line = lines[zone];
                int rgb;
                if (line.Length != 8 || line[6] != ' ' || (line[7] != '0' && line[7] != '1') ||
                    !int.TryParse(line.Substring(0, 6), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb) || rgb == 0)
                    return false;

                loadedColors[zone] = Color.FromArgb(unchecked((int)0xFF000000) | rgb);
                loadedOff[zone] = line[7] == '1';
            }

            loadedColors.CopyTo(colors, 0);
            loadedOff.CopyTo(off, 0);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void Save(Color[] colors, bool[] off)
    {
        SaveTo(PathToPreset, colors, off, true);
    }

    private static void SaveTo(string path, Color[] colors, bool[] off, bool replace)
    {
        var lines = new string[colors.Length];
        for (int zone = 0; zone < colors.Length; zone++)
            lines[zone] = (colors[zone].ToArgb() & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) + (off[zone] ? " 1" : " 0");

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            if (replace && File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
