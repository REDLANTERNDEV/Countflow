using Microsoft.AspNetCore.Identity;

namespace Countflow.Modules.Identity.Domain;

public sealed class CountflowRole : IdentityRole<Guid>
{
    public CountflowRole()
    {
    }

    public CountflowRole(string roleName)
        : base(roleName)
    {
    }
}
