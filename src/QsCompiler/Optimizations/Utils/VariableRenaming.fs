// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Quantum.QsCompiler.Experimental

open System
open System.Collections.Immutable
open System.Text.RegularExpressions
open Microsoft.Quantum.QsCompiler.DataTypes
open Microsoft.Quantum.QsCompiler.Experimental.Utils
open Microsoft.Quantum.QsCompiler.SyntaxTokens
open Microsoft.Quantum.QsCompiler.SyntaxTree
open Microsoft.Quantum.QsCompiler.Transformations.Core


/// The ScopeTransformation used to ensure unique variable names.
/// When called on a function body, will transform it such that all local variables defined
/// in the function body have unique names, generating new variable names if needed.
/// Autogenerated variable names have the form __qsVar[X]__[name]__.
type VariableRenaming private (_private_) =
    inherit SyntaxTreeTransformation()

    /// Returns a copy of the given variable stack with the given key set to the given value.
    /// Throws an ArgumentException if the given variable stack is empty.
    let set (key, value) = function
        | [] -> ArgumentException "No scope to define variables in" |> raise
        | head :: tail -> Map.add key value head :: tail

    /// A regex that matches the original name of a mangled variable name
    let varNameRegex = Regex("^__qsVar\d+__(.+)__$")

    /// Given a possibly-mangled variable name, returns the original variable name
    let demangle varName =
        let m = varNameRegex.Match varName
        if m.Success then m.Groups.[1].Value else varName

    /// The number of times a variable is referenced
    member val internal NewNamesSet = Set.empty with get, set
    /// The current dictionary of new names to substitute for variables
    member val internal RenamingStack = [Map.empty] with get, set
    /// Whether we should skip entering the next scope we encounter
    member val internal SkipScope = false with get, set

    /// Returns a copy of the given variable stack inside of a new scope
    member internal this.EnterScope map = Map.empty :: map

    /// Returns a copy of the given variable stack outside of the current scope.
    /// Throws an ArgumentException if the given variable stack is empty.
    member internal this.ExitScope = List.tail


    /// Given a new variable name, generates a new unique name and updates the state accordingly
    member this.GenerateUniqueName varName =
        let baseName = demangle varName
        let mutable num, newName = 0, baseName
        while this.NewNamesSet.Contains newName do
            num <- num + 1
            newName <- sprintf "__qsVar%d__%s__" num baseName
        this.NewNamesSet <- this.NewNamesSet.Add newName
        this.RenamingStack <- set (varName, newName) this.RenamingStack
        newName

    member this.Clear() =
        this.NewNamesSet <- Set.empty
        this.RenamingStack <- [Map.empty]

    new () as this = 
        new VariableRenaming("_private_") then
            this.Namespaces <- new VariableRenamingNamespaces(this)
            this.Statements <- new VariableRenamingStatements(this)
            this.StatementKinds <- new VariableRenamingStatementKinds(this)
            this.ExpressionKinds <- new VariableRenamingExpressionKinds(this)
            this.Types <- new TypeTransformation(this, TransformationOptions.Disabled)

/// private helper class for VariableRenaming
and private VariableRenamingNamespaces (parent : VariableRenaming) = 
    inherit NamespaceTransformation(parent)

    /// Processes the initial argument tuple from the function declaration
    let rec processArgTuple = function
        | QsTupleItem {VariableName = ValidName name} -> parent.GenerateUniqueName name.Value |> ignore
        | QsTupleItem {VariableName = InvalidName} -> ()
        | QsTuple items -> Seq.iter processArgTuple items

    override __.OnProvidedImplementation (argTuple, body) =
        parent.Clear()
        do processArgTuple argTuple
        base.OnProvidedImplementation (argTuple, body)

/// private helper class for VariableRenaming
and private VariableRenamingStatements (parent : VariableRenaming) = 
    inherit StatementTransformation(parent)

    override this.OnScope x =
        if parent.SkipScope then
            parent.SkipScope <- false
            base.OnScope x
        else
            parent.RenamingStack <- parent.EnterScope parent.RenamingStack
            let result = base.OnScope x
            parent.RenamingStack <- parent.ExitScope parent.RenamingStack
            result

/// private helper class for VariableRenaming
and private VariableRenamingStatementKinds (parent : VariableRenaming) = 
    inherit StatementKindTransformation(parent)

    override this.OnSymbolTuple syms =
        match syms with
        | VariableName item -> VariableName (NonNullable<_>.New (parent.GenerateUniqueName item.Value))
        | VariableNameTuple items -> Seq.map this.OnSymbolTuple items |> ImmutableArray.CreateRange |> VariableNameTuple
        | InvalidItem | DiscardedItem -> syms

    override this.OnRepeatStatement stm =
        parent.RenamingStack <- parent.EnterScope parent.RenamingStack
        parent.SkipScope <- true
        let result = base.OnRepeatStatement stm
        parent.RenamingStack <- parent.ExitScope parent.RenamingStack
        result

/// private helper class for VariableRenaming
and private VariableRenamingExpressionKinds (parent : VariableRenaming) = 
    inherit ExpressionKindTransformation(parent)
    
    /// Returns the value associated to the given key in the given variable stack.
    /// If the key is associated with multiple values, returns the one highest on the stack.
    /// Returns None if the key isn't associated with any values.
    let tryGet key = List.tryPick (Map.tryFind key)

    override this.OnIdentifier (sym, tArgs) =
        maybe {
            let! name =
                match sym with
                | LocalVariable name -> Some name.Value
                | _ -> None
            let! newName = tryGet name parent.RenamingStack
            return Identifier (LocalVariable (NonNullable<_>.New newName), tArgs)
        } |? Identifier (sym, tArgs)

