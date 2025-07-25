﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PharmacySystem.DomainLayer.Entities
{
    public class Admin : BaseEntity
    {
        [Required]
        public string? Name { get; set; }
        [Required, EmailAddress]
        public string? Email { get; set; }
        public string? Password { get; set; }
        [Required, Phone]
        public string? Phone { get; set; }
        
    }
}
