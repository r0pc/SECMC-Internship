namespace DataIntelligence.Core.Entities;

/// <summary>
/// One of the three access levels (NFR Security — "role-based access on the API"). Reference data,
/// seeded with the schema; roles are a property of the software, not something an operator adds.
/// </summary>
public class Role
{
    public byte RoleId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<UserRole> Users { get; set; } = [];
}

/// <summary>A role granted to a user.</summary>
public class UserRole
{
    public int UserId { get; set; }

    public byte RoleId { get; set; }

    public DateTime GrantedAtPkt { get; set; }

    public AppUser? User { get; set; }

    public Role? Role { get; set; }
}
