using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAccess.Models;

[PrimaryKey("UserId", "EmailAddress")]
[Table("emails")]
[Index("EmailAddress", Name = "unique_email_address", IsUnique = true)]
public partial class Email
{
    [Key]
    [Column("user_id")]
    public long UserId { get; set; }

    [Key]
    [Column("email_address")]
    [StringLength(255)]
    public string EmailAddress { get; set; } = null!;

    [Column("is_primary")]
    public bool IsPrimary { get; set; }

    [ForeignKey("UserId")]
    [InverseProperty("Emails")]
    public virtual User User { get; set; } = null!;
}
