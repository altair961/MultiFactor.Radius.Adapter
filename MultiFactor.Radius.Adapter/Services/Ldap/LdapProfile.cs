﻿using System.Collections.Generic;

namespace MultiFactor.Radius.Adapter.Services.Ldap
{
    public class LdapProfile
    {
        public LdapProfile()
        {
            LdapAttrs = new Dictionary<string, object>();
        }

        public string DistinguishedName { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }

        public LdapIdentity BaseDn { get; set; }

        public IDictionary<string, object> LdapAttrs { get; set; }
    }
}