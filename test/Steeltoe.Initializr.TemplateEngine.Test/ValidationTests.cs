﻿// Copyright 2017 the original author or authors.
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

using Steeltoe.Initializr.TemplateEngine.Models;
using Xunit;

namespace Steeltoe.Initializr.TemplateEngine.Test
{
    public class ValidationTests
    {
        [Fact]
        public void ProjectNameValidationTest()
        {
            var attrib = new ProjectNameValidationAttribute();
            var value = "123";
            var result = attrib.IsValid(value);

            Assert.False(result, "ProjectName cannot start with numbers");
        }

        [Fact]
        public void ProjectNameValidation_TestSegments()
        {
            var attrib = new ProjectNameValidationAttribute();
            var value = "Test.123";
            var result = attrib.IsValid(value);

            Assert.False(result, "No segment of ProjectName can start with numbers");
        }

        [Fact]
        public void ProjectNameValidation_TestHyphens()
        {
            var attrib = new ProjectNameValidationAttribute();
            var value = "Test-result.Foo";
            var result = attrib.IsValid(value);

            Assert.False(result, "No segment of ProjectName can contain hyphens");
        }

        [Fact]
        public void ProjectNameValidation_TestColons()
        {
            var attrib = new ProjectNameValidationAttribute();
            var value = "Test-result.Foo:boo";
            var result = attrib.IsValid(value);

            Assert.False(result, "No segment of ProjectName can contain :");
        }
    }
}
