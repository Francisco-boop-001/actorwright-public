using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace NpcManager.Application;

public sealed record NpcAidtPatch(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SkyrimNpcFinishCoreAggression? Aggression = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SkyrimNpcFinishCoreConfidence? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SkyrimNpcFinishCoreMorality? Morality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SkyrimNpcFinishCoreAssistance? Assistance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte? Energy = null)
{
    [JsonIgnore]
    public bool IsEmpty => Aggression is null && Confidence is null && Morality is null && Assistance is null && Energy is null;

    public ImmutableArray<MutationChange> ToExpectations()
    {
        var values = ImmutableArray.CreateBuilder<MutationChange>();
        Add("Aggression", Aggression is { } aggression ? (int)aggression : null);
        Add("Confidence", Confidence is { } confidence ? (int)confidence : null);
        Add("Morality", Morality is { } morality ? (int)morality : null);
        Add("Assistance", Assistance is { } assistance ? (int)assistance : null);
        Add("Energy", Energy);
        return values.ToImmutable();

        void Add(string field, int? value)
        {
            if (value is { } number)
                values.Add(new MutationChange("AIDT:" + field, null, number.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
