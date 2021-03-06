﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Security;

namespace NuGet.Services.Http.Authentication
{
    public class AdminKeyAuthenticationOptions : AuthenticationOptions
    {
        public static readonly string AuthTypeName = "AdminKey";

        public string Key { get; set; }
        public string GrantedRole { get; set; }
        public string GrantedUserName { get; set; }
        public bool AllowInsecure { get; set; }

        public AdminKeyAuthenticationOptions() : base(AuthTypeName) { }
    }
}
