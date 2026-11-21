using Microsoft.EntityFrameworkCore;

namespace Wishlist.Server.Data;

internal static class TestDeployment
{
    internal static async Task VerifyDatabaseAsync(AppDb db)
    {
        var isolated = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT (
                current_user = 'darim_test'
                AND session_user = 'darim_test'
                AND current_schemas(false) = ARRAY['darim_test']::name[]
                AND NOT has_schema_privilege(current_user, 'public', 'USAGE,CREATE')
                AND NOT EXISTS (
                    SELECT FROM pg_roles
                    WHERE rolname = current_user
                    AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
                )
                AND NOT EXISTS (
                    SELECT FROM pg_auth_members
                    WHERE member = to_regrole(current_user)
                )
            ) AS "Value"
            """).SingleAsync();

        if (!isolated)
            throw new InvalidOperationException(
                "DarimTest requires the isolated darim_test role and schema before running migrations.");
    }
}
