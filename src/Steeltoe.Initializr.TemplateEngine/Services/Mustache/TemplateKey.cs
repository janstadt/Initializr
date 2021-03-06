// Copyright 2017 the original author or authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Text.RegularExpressions;

namespace Steeltoe.Initializr.TemplateEngine.Services.Mustache
{
    public class TemplateKey
    {
        public string Steeltoe { get; }

        public string Template { get; }

        public string Framework { get; }

        public TemplateKey(string steeltoe, string framework, string template)
        {
            Steeltoe = Regex.Match(steeltoe, @"(\d+\.\d+).*").Groups[1].Value;
            Framework = framework;
            Template = template;
        }

        public override int GetHashCode()
        {
            return Steeltoe.GetHashCode() ^ Template.GetHashCode() ^ Framework.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return obj is TemplateKey key && Steeltoe.Equals(key.Steeltoe) && Template.Equals(key.Template) && Framework.Equals(key.Framework);
        }

        public override string ToString()
        {
            return $"TemplateKey[{Steeltoe},{Framework},{Template}]";
        }
    }
}
