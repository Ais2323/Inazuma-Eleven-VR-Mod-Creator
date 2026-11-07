using System;
using G4Composition;

public static partial class UniformMakerExporter
{
    static void Put(byte[] bytes, int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);

    static void ClearModelReference(CfgTable.Row row, int modelField, int textureField)
    {
        row.Types[modelField] = row.Types[textureField] = 1;
        row.Values[modelField] = row.Values[textureField] = 0L;
    }
}
