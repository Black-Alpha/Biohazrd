﻿using ClangSharp;
using ClangSharp.Interop;
using ClangSharp.Pathogen;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Biohazrd
{
    public sealed record TranslatedFunction : TranslatedDeclaration
    {
        public CallingConvention CallingConvention { get; }

        public TypeReference ReturnType { get; init; }
        public bool ReturnByReference { get; init; }
        public ImmutableArray<TranslatedParameter> Parameters { get; init; }

        public bool IsInstanceMethod { get; }
        public bool IsVirtual { get; }
        public bool IsConst { get; }
        public bool IsOperatorOverload { get; }

        public string DllFileName { get; init; } = "TODO.dll";
        public string MangledName { get; init; }

        internal TranslatedFunction(TranslatedFile file, FunctionDecl function)
            : base(file, function)
        {
            using (CXString mangling = function.Handle.Mangling)
            { MangledName = mangling.ToString(); }

            ReturnType = new ClangTypeReference(function.ReturnType);

            // Enumerate parameters
            ImmutableArray<TranslatedParameter>.Builder parametersBuilder = ImmutableArray.CreateBuilder<TranslatedParameter>(function.Parameters.Count);

            foreach (ParmVarDecl parameter in function.Parameters)
            { parametersBuilder.Add(new TranslatedParameter(file, parameter)); }

            Parameters = parametersBuilder.MoveToImmutable();

            // Get the function's calling convention
            string errorMessage;
            CXCallingConv clangCallingConvention = function.GetCallingConvention();
            CallingConvention = clangCallingConvention.ToDotNetCallingConvention(out errorMessage);

            if (errorMessage is not null)
            { throw new InvalidOperationException(errorMessage); }

            // Set method-specific properties
            if (function is CXXMethodDecl method)
            {
                Accessibility = method.Access.ToTranslationAccessModifier();
                IsInstanceMethod = !method.IsStatic;
                IsVirtual = method.IsVirtual;
                IsConst = method.IsConst;
            }
            // Non-method defaults
            else
            {
                Accessibility = AccessModifier.Public; // Don't use function.Access here, it's always private on non-method functions for some reason
                IsInstanceMethod = false;
                IsVirtual = false;
                IsConst = false;
            }

            // Determine if return value must be passed by reference
            ReturnByReference = function.ReturnType.MustBePassedByReference(isForInstanceMethodReturnValue: IsInstanceMethod);

            // Handle operator overloads
            ref PathogenOperatorOverloadInfo operatorOverloadInfo = ref function.GetOperatorOverloadInfo();

            if (operatorOverloadInfo.Kind != PathogenOperatorOverloadKind.None)
            {
                Name = $"operator_{operatorOverloadInfo.Name}";
                IsOperatorOverload = true;
            }
            else
            { IsOperatorOverload = false; }

            // Handle conversion operator overloads
            if (function is CXXConversionDecl)
            {
                Name = $"____ConversionOperator_{function.ReturnType}";
                IsOperatorOverload = true;
            }

            // Rename constructors/destructors
            if (function is CXXConstructorDecl)
            { Name = "Constructor"; }
            else if (function is CXXDestructorDecl)
            {
                Name = "Destructor";

                // clang_Cursor_getMangling returns the name of the vbase destructor (prefix ?_D), which is not what gets exported by MSVC and is thus useless for our purposes.
                // We instead want the normal destructor (prefix ?1) which is the only value returned by Clang for destructors when targeting the Microsoft ABI so we use the first mangling for them.
                // (In theory constructors are in a similar position where Clang uses a different Itanium-equivalent type to query the mangled name, but the C++ constructor name mangling doesn't change for these.)
                // Some more details are provided here: https://github.com/InfectedLibraries/Biohazrd/issues/12#issuecomment-782604833
                unsafe
                {
                    CXStringSet* manglings = null;
                    try
                    {
                        manglings = function.Handle.CXXManglings;
                        Debug.Assert(manglings->Count > 0);

                        if (manglings->Count > 0)
                        { MangledName = manglings->Strings[0].ToString(); }
                    }
                    finally
                    {
                        if (manglings != null)
                        { clang.disposeStringSet(manglings); }
                    }
                }
            }
        }

        public override IEnumerator<TranslatedDeclaration> GetEnumerator()
        {
            foreach (TranslatedParameter parameter in Parameters)
            { yield return parameter; }
        }

        public override string ToString()
        {
            string baseString = base.ToString();
            if (IsVirtual)
            { return $"Virtual Method {baseString}"; }
            else if (IsInstanceMethod)
            { return $"Instance Method {baseString}"; }
            else if (Declaration is CXXMethodDecl)
            { return $"Static Method {baseString}"; }
            else
            { return $"Function {baseString}"; }
        }
    }
}
