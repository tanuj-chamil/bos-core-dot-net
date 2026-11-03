using System.Globalization;
using System.Numerics;
using Bos.Contracts;

namespace Bos.Flows;

public sealed record FlowHelperDescriptor(string Name, int ContractVersion, int MinimumArguments, int MaximumArguments);

public static class FlowHelpers
{
    public static IReadOnlyList<FlowHelperDescriptor> Descriptors { get; } = System.Array.AsReadOnly(new[]
    {
        D("equal",2), D("notEqual",2), D("greaterThan",2), D("greaterOrEqual",2), D("lessThan",2), D("lessOrEqual",2),
        D("and",1,10000), D("or",1,10000), D("not",1), D("isNull",1), D("isMissing",1), D("isEmpty",1), D("coalesce",1,10000),
        D("add",2), D("subtract",2), D("multiply",2), D("divide",4), D("round",3), D("abs",1),
        D("length",1), D("contains",2), D("sum",1), D("min",1), D("max",1), D("pluck",2),
        D("trim",1), D("concat",1,10000), D("lower",1), D("upper",1), D("startsWith",2), D("endsWith",2),
        D("today",0,1), D("now",0), D("addDays",2), D("daysBetween",2), D("toString",1),
        D("parseInteger",1), D("parseDecimal",1), D("parseDate",1)
    });
    private static FlowHelperDescriptor D(string name, int min, int? max = null) => new(name, 1, min, max ?? min);
    internal static FlowHelperDescriptor Get(string name, int count)
    {
        var descriptor = Descriptors.FirstOrDefault(x => x.Name == name) ?? throw FlowYaml.Error("Unknown helper: " + name);
        if (count < descriptor.MinimumArguments || count > descriptor.MaximumArguments) throw FlowYaml.Error("Invalid helper argument count: " + name);
        return descriptor;
    }
    internal static FlowValue Invoke(string name, IReadOnlyList<FlowValue> args, InvocationContext context)
    {
        Get(name, args.Count);
        bool acceptsMissing = name is "isMissing" or "isNull" or "isEmpty" or "coalesce";
        if (!acceptsMissing && args.Any(x => x.Kind == ValueKind.Missing)) throw new BosException("MISSING_VALUE", "Helper received Missing.");
        FlowValue A(int i) => args[i];
        string S(int i) => A(i).Require<string>(ValueKind.String);
        long I(int i) => A(i).Require<long>(ValueKind.Integer);
        ExactDecimal N(int i) => A(i).Require<ExactDecimal>(ValueKind.Decimal);
        FlowValue B(bool b) => FlowValue.Bool(b);
        try
        {
            switch (name)
            {
                case "equal": return B(Equal(A(0), A(1)));
                case "notEqual": return B(!Equal(A(0), A(1)));
                case "greaterThan": return B(Compare(A(0), A(1)) > 0);
                case "greaterOrEqual": return B(Compare(A(0), A(1)) >= 0);
                case "lessThan": return B(Compare(A(0), A(1)) < 0);
                case "lessOrEqual": return B(Compare(A(0), A(1)) <= 0);
                case "and": return B(args.All(x => x.Require<bool>(ValueKind.Boolean)));
                case "or": return B(args.Any(x => x.Require<bool>(ValueKind.Boolean)));
                case "not": return B(!A(0).Require<bool>(ValueKind.Boolean));
                case "isNull": return B(A(0).Kind == ValueKind.Null);
                case "isMissing": return B(A(0).Kind == ValueKind.Missing);
                case "isEmpty": return B(A(0).Kind is ValueKind.Missing or ValueKind.Null || A(0).Kind switch
                {
                    ValueKind.String => S(0).Length == 0, ValueKind.Array => A(0).Items.Length == 0,
                    ValueKind.Object => A(0).Fields.Count == 0, _ => false
                });
                case "coalesce": return args.FirstOrDefault(x => x.Kind is not (ValueKind.Null or ValueKind.Missing)) ?? FlowValue.Null;
                case "add": case "subtract": case "multiply":
                    SameNumeric(A(0), A(1));
                    if (A(0).Kind == ValueKind.Integer)
                        return FlowValue.Integer(name switch { "add" => checked(I(0) + I(1)), "subtract" => checked(I(0) - I(1)), _ => checked(I(0) * I(1)) });
                    return FlowValue.Decimal(name switch { "add" => N(0).Add(N(1)), "subtract" => N(0).Add(N(1), true), _ => N(0).Multiply(N(1)) });
                case "divide": return FlowValue.Decimal(N(0).Divide(N(1), checked((int)I(2)), S(3)));
                case "round":
                    if (A(0).Kind == ValueKind.Integer)
                    {
                        if (I(1) is < 0 or > 12 || S(2) is not ("halfEven" or "halfAwayFromZero" or "towardZero"))
                            throw new BosException("INVALID_ARGUMENT", "Invalid scale or rounding mode.");
                        return A(0);
                    }
                    return FlowValue.Decimal(N(0).Round(checked((int)I(1)), S(2)));
                case "abs":
                    if (A(0).Kind == ValueKind.Integer) return FlowValue.Integer(checked(Math.Abs(I(0))));
                    return FlowValue.Decimal(ExactDecimal.Checked(BigInteger.Abs(N(0).Coefficient), N(0).Scale));
                case "length": return FlowValue.Integer(A(0).Items.Length);
                case "contains":
                    foreach (var item in A(0).Items) { context.Budget.Node(); if (Equal(item, A(1))) return B(true); }
                    return B(false);
                case "sum": case "min": case "max":
                {
                    var items = A(0).Items;
                    if (items.Length == 0)
                    {
                        if (name == "sum") return FlowValue.Decimal(new(0, 0));
                        throw new BosException("INVALID_ARGUMENT", "min/max require a nonempty list.");
                    }
                    var result = items[0]; SameNumeric(result, result); context.Budget.Node();
                    foreach (var item in items.Skip(1))
                    {
                        context.Budget.Node(); SameNumeric(result, item);
                        result = name == "sum" ? Invoke("add", new[] { result, item }, context) :
                            (name == "min" ? Compare(item, result) < 0 : Compare(item, result) > 0) ? item : result;
                    }
                    return result;
                }
                case "pluck":
                {
                    string path = S(1); FlowCompiler.CheckPath(path);
                    return FlowValue.Array(A(0).Items.Select(item =>
                    {
                        context.Budget.Node(); var value = item.Path(path);
                        if (value.Kind == ValueKind.Missing) throw new BosException("MISSING_VALUE", "pluck selected an absent field.");
                        if (value.Kind is ValueKind.Array or ValueKind.Object) throw new BosException("TYPE_MISMATCH", "pluck requires scalar fields.");
                        return value;
                    }));
                }
                case "trim": return FlowValue.Text(S(0).Trim());
                case "lower": return FlowValue.Text(S(0).ToLowerInvariant());
                case "upper": return FlowValue.Text(S(0).ToUpperInvariant());
                case "concat":
                {
                    int length = 0;
                    foreach (var arg in args)
                    {
                        length = checked(length + arg.Require<string>(ValueKind.String).Length);
                        if (length > 262144) throw new BosException("EXECUTION_LIMIT", "Concatenation exceeds allocation limit.");
                    }
                    return FlowValue.Text(string.Concat(args.Select(x => (string)x.Data!)));
                }
                case "startsWith": return B(S(0).StartsWith(S(1), StringComparison.Ordinal));
                case "endsWith": return B(S(0).EndsWith(S(1), StringComparison.Ordinal));
                case "now": return FlowValue.Instant(context.StartedAt);
                case "today":
                    return FlowValue.Date(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.StartedAt,
                        TimeZoneInfo.FindSystemTimeZoneById(args.Count == 0 ? context.WorkspaceTimezone : S(0))).DateTime));
                case "addDays": return FlowValue.Date(A(0).Require<DateOnly>(ValueKind.Date).AddDays(checked((int)I(1))));
                case "daysBetween": return FlowValue.Integer(A(1).Require<DateOnly>(ValueKind.Date).DayNumber - A(0).Require<DateOnly>(ValueKind.Date).DayNumber);
                case "toString": return FlowValue.Text(A(0).ScalarText());
                case "parseInteger": return FlowValue.Integer(FlowValue.ParseInteger(S(0)));
                case "parseDecimal": return FlowValue.Decimal(ExactDecimal.Parse(S(0)));
                case "parseDate":
                    if (!DateOnly.TryParseExact(S(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        throw new BosException("CONVERSION_FAILED", "Invalid Gregorian date.");
                    return FlowValue.Date(date);
                default: throw FlowYaml.Error("Unknown helper.");
            }
        }
        catch (OverflowException) { throw new BosException("ARITHMETIC_OVERFLOW", "Checked arithmetic overflow."); }
        catch (ArgumentOutOfRangeException) { throw new BosException("ARITHMETIC_OVERFLOW", "Calendar arithmetic overflow."); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new BosException("INVALID_ARGUMENT", "Unknown or invalid timezone."); }
    }

    private static void SameNumeric(FlowValue a, FlowValue b)
    {
        if (a.Kind != b.Kind || a.Kind is not (ValueKind.Integer or ValueKind.Decimal))
            throw new BosException("TYPE_MISMATCH", "Arithmetic requires the same numeric kind.");
    }
    private static bool Equal(FlowValue a, FlowValue b)
    {
        if (a.Kind == ValueKind.Missing || b.Kind == ValueKind.Missing) throw new BosException("MISSING_VALUE", "Cannot compare Missing.");
        if (a.Kind is ValueKind.Object or ValueKind.Array || b.Kind is ValueKind.Object or ValueKind.Array)
            throw new BosException("TYPE_MISMATCH", "Equality requires scalar operands.");
        if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null) return a.Kind == b.Kind;
        if (a.Kind != b.Kind)
            throw new BosException("TYPE_MISMATCH", "Equality requires same-kind scalars.");
        return a.Kind == ValueKind.Decimal ? ((ExactDecimal)a.Data!).Compare((ExactDecimal)b.Data!) == 0 : Equals(a.Data, b.Data);
    }
    private static int Compare(FlowValue a, FlowValue b)
    {
        if (a.Kind != b.Kind) throw new BosException("TYPE_MISMATCH", "Ordering requires same-kind scalars.");
        return a.Kind switch
        {
            ValueKind.String => StringComparer.Ordinal.Compare((string)a.Data!, (string)b.Data!),
            ValueKind.Integer => ((long)a.Data!).CompareTo((long)b.Data!),
            ValueKind.Decimal => ((ExactDecimal)a.Data!).Compare((ExactDecimal)b.Data!),
            ValueKind.Date => ((DateOnly)a.Data!).CompareTo((DateOnly)b.Data!),
            ValueKind.Instant => ((DateTimeOffset)a.Data!).CompareTo((DateTimeOffset)b.Data!),
            ValueKind.Missing => throw new BosException("MISSING_VALUE", "Cannot order Missing."),
            _ => throw new BosException("TYPE_MISMATCH", "Value is not an ordered scalar.")
        };
    }
}
