using Npgsql;

namespace Spms.Persistence;

/// <summary>
/// Translates PostgreSQL refusals into something a module can answer with.
/// The schema enforces its invariants itself (exclusion constraints, checks,
/// triggers); the code's job is to name the refusal, not to pre-empt it.
/// </summary>
public static class PostgresErrors
{
    public const string ExclusionViolation = "23P01";
    public const string UniqueViolation = "23505";
    public const string CheckViolation = "23514";
    public const string ForeignKeyViolation = "23503";
    public const string SerializationFailure = "40001";
    public const string InsufficientPrivilege = "42501";

    public static PostgresException? Find(Exception? e)
    {
        while (e is not null)
        {
            if (e is PostgresException pg) return pg;
            e = e.InnerException;
        }
        return null;
    }

    public static bool Is(Exception e, string sqlState, string? constraintFragment = null)
    {
        var pg = Find(e);
        return pg?.SqlState == sqlState
               && (constraintFragment is null || (pg.ConstraintName?.Contains(constraintFragment, StringComparison.Ordinal) ?? false));
    }

    /// <summary>A constraint name, for mapping a refusal onto a rule code.</summary>
    public static string? Constraint(Exception e) => Find(e)?.ConstraintName;
}
