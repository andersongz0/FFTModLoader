using System.Buffers;
using System.Text.Json;

namespace FFTModLoader.ContentExpansion;

// Fixed schema: no reflection, JsonSerializerOptions metadata cache or dynamic
// converter lookup inside native save callbacks. Keep schema 1 byte-compatible.
public static class ExpandedSaveJson
{
    internal sealed record Document(int SchemaVersion, SaveSlotIdentity Slot,
        string NativePayloadSha256, string StateSha256, ExpandedSaveState State);

    public static byte[] SlotBytes(SaveSlotIdentity slot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue(slot.ProfileKey); writer.WriteStringValue(slot.NativeSlotName);
        writer.WriteEndArray(); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] StateBytes(ExpandedSaveState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteState(writer, state); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] DocumentBytes(Document document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject(); writer.WriteNumber("SchemaVersion", document.SchemaVersion);
        writer.WriteStartObject("Slot");
        writer.WriteString("ProfileKey", document.Slot.ProfileKey);
        writer.WriteString("NativeSlotName", document.Slot.NativeSlotName); writer.WriteEndObject();
        writer.WriteString("NativePayloadSha256", document.NativePayloadSha256);
        writer.WriteString("StateSha256", document.StateSha256);
        writer.WritePropertyName("State"); WriteState(writer, document.State);
        writer.WriteEndObject(); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteState(Utf8JsonWriter writer, ExpandedSaveState state)
    {
        writer.WriteStartObject(); writer.WriteStartObject("Items");
        foreach (var item in state.Items) writer.WriteNumber(item.Key, item.Value);
        writer.WriteEndObject(); writer.WriteStartArray("Units");
        foreach (var unit in state.Units)
        {
            writer.WriteStartObject(); writer.WriteNumber("SerializedSlot", unit.SerializedSlot);
            writer.WriteString("NativeRecordSha256", unit.NativeRecordSha256);
            writer.WriteStartArray("LearnedKeys");
            foreach (string key in unit.LearnedKeys) writer.WriteStringValue(key);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static SaveSlotIdentity ReadSlot(JsonElement value)
    {
        RequireObject(value, "ProfileKey", "NativeSlotName");
        return new(Text(value.GetProperty("ProfileKey")), Text(value.GetProperty("NativeSlotName")));
    }

    internal static Document ReadDocument(JsonElement value)
    {
        RequireObject(value, "SchemaVersion", "Slot", "NativePayloadSha256", "StateSha256", "State");
        var state = value.GetProperty("State"); RequireObject(state, "Items", "Units");
        var itemsElement = state.GetProperty("Items");
        if (itemsElement.ValueKind != JsonValueKind.Object) throw Invalid();
        var items = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in itemsElement.EnumerateObject())
            if (!items.TryAdd(item.Name, Number(item.Value))) throw Invalid();
        var unitsElement = state.GetProperty("Units");
        if (unitsElement.ValueKind != JsonValueKind.Array || unitsElement.GetArrayLength() > 54) throw Invalid();
        var units = new List<SavedUnitLearning>();
        foreach (var unit in unitsElement.EnumerateArray())
        {
            RequireObject(unit, "SerializedSlot", "NativeRecordSha256", "LearnedKeys");
            var keys = unit.GetProperty("LearnedKeys");
            if (keys.ValueKind != JsonValueKind.Array) throw Invalid();
            units.Add(new(Number(unit.GetProperty("SerializedSlot")), Text(unit.GetProperty("NativeRecordSha256")),
                keys.EnumerateArray().Select(Text).ToArray()));
        }
        return new(Number(value.GetProperty("SchemaVersion")), ReadSlot(value.GetProperty("Slot")),
            Text(value.GetProperty("NativePayloadSha256")), Text(value.GetProperty("StateSha256")), new(items, units.ToArray()));
    }

    private static void RequireObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Invalid();
        if (seen.Count != names.Length) throw Invalid();
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Invalid();
    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
        ? number : throw Invalid();
    private static InvalidDataException Invalid() => new("Malformed expanded save schema: missing, duplicate, unknown or incorrectly typed property.");
}
