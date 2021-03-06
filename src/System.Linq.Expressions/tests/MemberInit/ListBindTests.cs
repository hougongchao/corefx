﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace System.Linq.Expressions.Tests
{
    public class ListBindTests
    {
        private class ListWrapper<T>
        {
            public static List<T> StaticListField = new List<T>();
            public static List<T> StaticListProperty { get; set; }
            public readonly List<T> ListField = new List<T>();

            public List<T> ListProperty
            {
                get { return ListField; }
            }

            public HashSet<T> HashSetField = new HashSet<T>();

            public List<T> WriteOnlyList
            {
                set { }
            }

            public List<T> GetList()
            {
                return ListField;
            }

            public IEnumerable<T> EnumerableProperty
            {
                get { return ListField; }
            }
        }

        [Fact]
        public void MethodInfoNull()
        {
            ElementInit elInit = Expression.ElementInit(typeof(List<int>).GetMethod(nameof(List<int>.Add)), Expression.Constant(0));
            Assert.Throws<ArgumentNullException>("propertyAccessor", () => Expression.ListBind(default(MethodInfo), elInit));
            Assert.Throws<ArgumentNullException>("propertyAccessor", () => Expression.ListBind(default(MethodInfo), Enumerable.Repeat(elInit, 1)));
        }

        [Fact]
        public void MemberInfoNull()
        {
            ElementInit elInit = Expression.ElementInit(typeof(List<int>).GetMethod(nameof(List<int>.Add)), Expression.Constant(0));
            Assert.Throws<ArgumentNullException>("member", () => Expression.ListBind(default(MemberInfo), elInit));
            Assert.Throws<ArgumentNullException>("member", () => Expression.ListBind(default(MemberInfo), Enumerable.Repeat(elInit, 1)));
        }

        [Fact]
        public void InitializersNull()
        {
            PropertyInfo property = typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.ListProperty));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.ListProperty))[0];
            Assert.Throws<ArgumentNullException>("initializers", () => Expression.ListBind(property, default(ElementInit[])));
            Assert.Throws<ArgumentNullException>("initializers", () => Expression.ListBind(property, default(IEnumerable<ElementInit>)));
            Assert.Throws<ArgumentNullException>("initializers", () => Expression.ListBind(member, default(ElementInit[])));
            Assert.Throws<ArgumentNullException>("initializers", () => Expression.ListBind(member, default(IEnumerable<ElementInit>)));
        }

        [Fact]
        public void MethodForMember()
        {
            MethodInfo method = typeof(ListWrapper<int>).GetMethod(nameof(ListWrapper<int>.GetList));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.GetList))[0];
            ElementInit elInit = Expression.ElementInit(typeof(List<int>).GetMethod("Add"), Expression.Constant(0));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(method, elInit));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(method, Enumerable.Repeat(elInit, 1)));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, elInit));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, Enumerable.Repeat(elInit, 1)));
        }

        [Fact]
        public void NonEnumerableListType()
        {
            PropertyInfo property = typeof(string).GetProperty(nameof(string.Length));
            MemberInfo member = typeof(string).GetMember(nameof(string.Length))[0];
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property, Enumerable.Empty<ElementInit>()));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, Enumerable.Empty<ElementInit>()));
        }

        private static IEnumerable<object> NonAddableListExpressions()
        {
            PropertyInfo property = typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.EnumerableProperty));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.EnumerableProperty))[0];
            yield return new object[] {Expression.ListBind(property)};
            yield return new object[] {Expression.ListBind(property, Enumerable.Empty<ElementInit>())};
            yield return new object[] {Expression.ListBind(member)};
            yield return new object[] {Expression.ListBind(member, Enumerable.Empty<ElementInit>())};
        }

        [Theory, PerCompilationType(nameof(NonAddableListExpressions))]
        public void NonAddableListType(MemberListBinding listBinding, bool useInterpreter)
        {
            Func<ListWrapper<int>> func = Expression.Lambda<Func<ListWrapper<int>>>(
                            Expression.MemberInit(
                                Expression.New(typeof(ListWrapper<int>)),
                                listBinding
                                )
                            ).Compile(useInterpreter);
            Assert.Empty(func().EnumerableProperty);
        }

        [Fact]
        public void NullElement()
        {
            Assert.Throws<ArgumentNullException>(() => Expression.ListBind(typeof(ListWrapper<int>).GetMethod(nameof(ListWrapper<int>.GetList)), null));
        }

        [Fact]
        public void MismatchingElement()
        {
            Assert.Throws<ArgumentException>(() => Expression.ListBind(typeof(ListWrapper<int>).GetMethod(nameof(ListWrapper<int>.GetList)), Expression.ElementInit(typeof(HashSet<int>).GetMethod("Add"), Expression.Constant(1))));
        }

        [Fact]
        public void BindMethodMustBeProperty()
        {
            MemberInfo toString = typeof(object).GetMember(nameof(ToString))[0];
            MethodInfo toStringMeth = typeof(object).GetMethod(nameof(ToString));
            Assert.Throws<ArgumentException>(() => Expression.ListBind(toString));
            Assert.Throws<ArgumentException>(() => Expression.ListBind(toString, Enumerable.Empty<ElementInit>()));
            Assert.Throws<ArgumentException>(() => Expression.ListBind(toStringMeth));
            Assert.Throws<ArgumentException>(() => Expression.ListBind(toStringMeth, Enumerable.Empty<ElementInit>()));
        }

        public static IEnumerable<object[]> ZeroInitializerExpressions()
        {
            PropertyInfo property = typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.ListProperty));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.ListProperty))[0];
            MemberInfo fieldMember = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.ListField))[0];
            yield return new object[] {Expression.ListBind(property)};
            yield return new object[] {Expression.ListBind(property, Enumerable.Empty<ElementInit>())};
            yield return new object[] {Expression.ListBind(member)};
            yield return new object[] {Expression.ListBind(member, Enumerable.Empty<ElementInit>())};
            yield return new object[] {Expression.ListBind(fieldMember)};
            yield return new object[] {Expression.ListBind(fieldMember, Enumerable.Empty<ElementInit>())};
        }

        [Theory]
        [PerCompilationType("ZeroInitializerExpressions")]
        public void ZeroInitializersIsValid(MemberListBinding binding, bool useInterpreter)
        {
            Func<ListWrapper<int>> func = Expression.Lambda<Func<ListWrapper<int>>>(
                Expression.MemberInit(
                    Expression.New(typeof(ListWrapper<int>)),
                    binding
                    )
                ).Compile(useInterpreter);
            Assert.Empty(func().ListProperty);
        }

        [Fact]
        public void UnreadableListProperty()
        {
            PropertyInfo property = typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.WriteOnlyList));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.WriteOnlyList))[0];
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property, new ElementInit[0]));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property, Enumerable.Empty<ElementInit>()));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, new ElementInit[0]));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, Enumerable.Empty<ElementInit>()));
        }

        [Fact]
        [ActiveIssue(5963)]
        public void StaticListProperty()
        {
            PropertyInfo property = typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.StaticListProperty));
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.StaticListProperty))[0];
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property, new ElementInit[0]));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(property, Enumerable.Empty<ElementInit>()));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, new ElementInit[0]));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, Enumerable.Empty<ElementInit>()));
        }

        [Fact]
        [ActiveIssue(5963)]
        public void StaticListField()
        {
            MemberInfo member = typeof(ListWrapper<int>).GetMember(nameof(ListWrapper<int>.StaticListProperty))[0];
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, new ElementInit[0]));
            Assert.Throws<ArgumentException>(null, () => Expression.ListBind(member, Enumerable.Empty<ElementInit>()));
        }

        [Theory, ClassData(typeof(CompilationTypes))]
        public void InitializeVoidAdd(bool useInterperter)
        {
            Expression<Func<ListWrapper<int>>> listInit = () => new ListWrapper<int> {ListProperty = {1, 4, 9, 16}};
            Func<ListWrapper<int>> func = listInit.Compile(useInterperter);
            Assert.Equal(new[] {1, 4, 9, 16}, func().ListProperty);
        }

        [Theory, ClassData(typeof(CompilationTypes))]
        public void InitializeNonVoidAdd(bool useInterpreter)
        {
            Expression<Func<ListWrapper<int>>> hashInit = () => new ListWrapper<int> {HashSetField = {1, 4, 9, 16}};
            Func<ListWrapper<int>> func = hashInit.Compile(useInterpreter);
            Assert.Equal(new[] {1, 4, 9, 16}, func().HashSetField.OrderBy(i => i));
        }

        [Fact]
        public void UpdateDifferentReturnsDifferent()
        {
            MemberListBinding binding = Expression.ListBind(typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.ListProperty)), Enumerable.Range(0, 3).Select(i => Expression.ElementInit(typeof(List<int>).GetMethod("Add"), Expression.Constant(i))));
            Assert.NotSame(binding, binding.Update(new[] {Expression.ElementInit(typeof(List<int>).GetMethod(nameof(List<int>.Add)), Expression.Constant(1))}));
        }

        [Fact]
        public void UpdateSameReturnsSame()
        {
            MemberListBinding binding = Expression.ListBind(typeof(ListWrapper<int>).GetProperty(nameof(ListWrapper<int>.ListProperty)), Enumerable.Range(0, 3).Select(i => Expression.ElementInit(typeof(List<int>).GetMethod("Add"), Expression.Constant(i))));
            Assert.Same(binding, binding.Update(binding.Initializers));
        }
    }
}
