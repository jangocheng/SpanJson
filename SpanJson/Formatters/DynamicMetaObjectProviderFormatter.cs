﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.CSharp.RuntimeBinder;
using SpanJson.Formatters.Dynamic;
using SpanJson.Resolvers;

namespace SpanJson.Formatters
{
    public class DynamicMetaObjectProviderFormatter<T, TSymbol, TResolver> : BaseFormatter, IJsonFormatter<T, TSymbol, TResolver>
        where T : IDynamicMetaObjectProvider
        where TResolver : IJsonFormatterResolver<TSymbol, TResolver>, new()
        where TSymbol : struct
    {
        private static readonly Func<T> CreateFunctor = BuildCreateFunctor<T>(null);

        public static readonly DynamicMetaObjectProviderFormatter<T, TSymbol, TResolver> Default =
            new DynamicMetaObjectProviderFormatter<T, TSymbol, TResolver>();

        private static readonly TResolver Resolver = StandardResolvers.GetResolver<TSymbol, TResolver>();
        private static readonly IJsonFormatter<T, TSymbol, TResolver> DefaultFormatter = Resolver.GetFormatter<T>();
        private static readonly Dictionary<string, DeserializeDelegate> KnownMembersDictionary = BuildKnownMembers();

        private static Dictionary<string, DeserializeDelegate> BuildKnownMembers()
        {
            var resolver = StandardResolvers.GetResolver<TSymbol, TResolver>();
            var memberInfos = resolver.GetMemberInfos<T>().ToList();
            var inputParameter = Expression.Parameter(typeof(T), "input");
            var readerParameter = Expression.Parameter(typeof(JsonReader<TSymbol>).MakeByRefType(), "reader");
            var result = new Dictionary<string, DeserializeDelegate>();
            // can't deserialize abstract or interface
            foreach (var jsonMemberInfo in memberInfos)
            {
                if (!jsonMemberInfo.CanWrite)
                {
                    var skipNextMethodInfo = FindPublicInstanceMethod(readerParameter.Type, nameof(JsonReader<TSymbol>.SkipNextSegment));
                    var skipExpression = Expression.Lambda<DeserializeDelegate>(Expression.Call(readerParameter, skipNextMethodInfo), inputParameter, readerParameter).Compile();
                    result.Add(jsonMemberInfo.Name, skipExpression);
                }
                else if (jsonMemberInfo.MemberType.IsAbstract || jsonMemberInfo.MemberType.IsInterface)
                {
                    var throwExpression = Expression.Lambda<DeserializeDelegate>(Expression.Block(
                            Expression.Throw(Expression.Constant(new NotSupportedException($"{typeof(T).Name} contains abstract or interface members."))),
                            Expression.Default(typeof(T))),
                        inputParameter, readerParameter).Compile();
                    result.Add(jsonMemberInfo.Name, throwExpression);
                }
                else
                {
                    var formatter = ((IJsonFormatterResolver) resolver).GetFormatter(jsonMemberInfo.MemberType);
                    var assignExpression = Expression.Assign(Expression.PropertyOrField(inputParameter, jsonMemberInfo.MemberName),
                        Expression.Call(Expression.Constant(formatter), formatter.GetType().GetMethod("Deserialize"), readerParameter));
                    var lambda = Expression.Lambda<DeserializeDelegate>(assignExpression, inputParameter, readerParameter).Compile();
                    result.Add(jsonMemberInfo.Name, lambda);
                }
            }

            return result;
        }

        public T Deserialize(ref JsonReader<TSymbol> reader)
        {
            if (reader.ReadIsNull())
            {
                return default;
            }

            reader.ReadBeginObjectOrThrow();
            var result = CreateFunctor();
            var count = 0;
            while (!reader.TryReadIsEndObjectOrValueSeparator(ref count))
            {
                var name = reader.ReadEscapedName();
                if (KnownMembersDictionary.TryGetValue(name, out var action))
                {
                    action(result, ref reader); // if we have known members we try to assign them directly without dynamic
                }
                else
                {
                    SetObjectDynamically(name, result, reader.ReadDynamic()); // todo improve?
                }
            }

            return result;
        }


        public void Serialize(ref JsonWriter<TSymbol> writer, T value)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            if (value is ISpanJsonDynamicValue<TSymbol> dynValue) // if we serialize our dynamic value again we simply write the symbols directly
            {
                writer.WriteVerbatim(dynValue.Symbols);
            }
            else
            {
                var memberInfos = Resolver.GetDynamicMemberInfos(value);
                var counter = 0;
                writer.WriteBeginObject();
                foreach (var memberInfo in memberInfos)
                {
                    var child = GetObjectDynamically(memberInfo.MemberName, value);
                    if (memberInfo.ExcludeNull && child == null)
                    {
                        continue;
                    }

                    if (counter++ > 0)
                    {
                        writer.WriteValueSeparator();
                    }

                    writer.WriteName(memberInfo.Name);
                    RuntimeFormatter<TSymbol, TResolver>.Default.Serialize(ref writer, child);
                }

                writer.WriteEndObject();
            }
        }

        private static object GetObjectDynamically(string memberName, T target)
        {
            var binder = Binder.GetMember(CSharpBinderFlags.None, memberName, null,
                new[] {CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)});
            var callsite = CallSite<Func<CallSite, object, object>>.Create(binder);
            return callsite.Target(callsite, target);
        }


        private static void SetObjectDynamically(string memberName, T target, object value)
        {
            var binder = Binder.SetMember(CSharpBinderFlags.None, memberName, null,
                new[]
                {
                    CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null),
                    CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)
                });
            var callsite = CallSite<Func<CallSite, object, object, object>>.Create(binder);
            callsite.Target(callsite, target, value);
        }

        protected delegate void DeserializeDelegate(T input, ref JsonReader<TSymbol> reader);
    }
}