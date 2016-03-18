﻿using Pchp.CodeAnalysis.Semantics.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Semantics;
using System.Diagnostics;
using Pchp.CodeAnalysis.Semantics;
using Pchp.Syntax.Text;

namespace Pchp.CodeAnalysis.FlowAnalysis
{
    public partial class EdgesAnalysis : CFGWalker
    {
        #region Fields

        /// <summary>
        /// The worklist to be used to enqueue next blocks.
        /// </summary>
        readonly Worklist<BoundBlock> _worklist;

        /// <summary>
        /// Gets current type context for type masks resolving.
        /// </summary>
        internal TypeRefContext TypeRefContext => _state.TypeRefContext;

        /// <summary>
        /// Current flow state.
        /// </summary>
        internal FlowState State => _state;
        FlowState _state;

        /// <summary>
        /// Gets underlaying <see cref="ExpressionAnalysis"/>.
        /// </summary>
        internal ExpressionAnalysis OpAnalysis => (ExpressionAnalysis)_opvisitor;

        #endregion

        #region Construction

        /// <summary>
        /// Creates an instance of <see cref="EdgesAnalysis"/> that can analyse a block.
        /// </summary>
        /// <param name="worklist">The worklist to be used to enqueue next blocks.</param>
        /// <returns>New instance of the flow analyzer.</returns>
        internal static EdgesAnalysis Create(Worklist<BoundBlock> worklist)
        {
            Contract.ThrowIfNull(worklist);

            var opvisitor = new ExpressionAnalysis();   // TODO: MEF Import?
            var analysis = new EdgesAnalysis(worklist, opvisitor);
            opvisitor.SetAnalysis(analysis);

            //
            return analysis;
        }

        private EdgesAnalysis(Worklist<BoundBlock> worklist, OperationVisitor opvisitor)
            : base(opvisitor)
        {
            _worklist = worklist;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Debug assert the state is initialized therefore we are in the middle on a block analysis.
        /// </summary>
        void AssertState()
        {
            Debug.Assert(_state != null);
        }

        /// <summary>
        /// Sets type of local variable in current state.
        /// </summary>
        protected virtual TypeRefMask SetVar(string name, TypeRefMask typemask)
        {
            AssertState();

            _state.SetVar(name, typemask);
            return typemask;
        }

        /// <summary>
        /// Helper method that merges state with the target block and determines whether to continue by visiting the target block.
        /// </summary>
        /// <param name="state">Locals state in which we are entering the target.</param>
        /// <param name="target">Target block.</param>
        /// <remarks>Only for traversing into blocks within the same routine (same type context).</remarks>
        private void TraverseToBlock(FlowState/*!*/state, BoundBlock/*!*/target)
        {
            Contract.ThrowIfNull(state);    // state should be already set by previous block
            
            var targetState = target.FlowState;
            if (targetState != null)
            {
                Debug.Assert(targetState.Common == state.Common);

                // block was visited already,
                // merge and check whether state changed
                state = state.Merge(targetState);   // merge states into new one
                if (state.Equals(targetState))
                    return; // state convergated, we don't have to analyse target block again
            }
            else
            {
                // block was not visited yet
                state = state.Clone();              // copy state into new one
            }

            // update target block state
            target.FlowState = state;

            //
            _worklist.Enqueue(target);
        }

        /// <summary>
        /// Called to initialize <see cref="VisitCFGBlock"/> call.
        /// Sets <see cref="_state"/> to known initial block state.
        /// </summary>
        protected virtual void VisitCFGBlockInit(BoundBlock/*!*/x)
        {
            Contract.ThrowIfNull(x.FlowState);   // state should be already set by previous edge
            _state = x.FlowState.Clone();        // TFlowState for the statements in the block            
        }

        #endregion

        #region GraphVisitor Members

        public override void VisitCFG(ControlFlowGraph x)
        {
            Contract.ThrowIfNull(x);
            Debug.Assert(x.Start.FlowState != null, "Start block has to have an initial state set.");

            _worklist.Enqueue(x.Start);
        }

        public override void VisitCFGBlock(BoundBlock x)
        {
            VisitCFGBlockInit(x);
            VisitCFGBlockInternal(x);   // modifies _state, traverses to the edge
        }

        public override void VisitCFGCaseBlock(CaseBlock x)
        {
            VisitCFGBlockInit(x);
            Accept(x.CaseValue);
            VisitCFGBlockInternal(x);
        }

        public override void VisitCFGCatchBlock(CatchBlock x)
        {
            VisitCFGBlockInit(x);

            // add catch control variable to the state
            _state.SetVar(x.VariableName.Value, this.TypeRefContext.GetTypeMask(x.TypeRef, true));
            _state.SetVarUsed(x.VariableName.Value);  // marks variable as Used since in PHP you can't have catch block without specifying the variable we won't report unused catch variable
            VisitCFGBlockInternal(x);
        }

        public override void VisitCFGSimpleEdge(SimpleEdge x)
        {
            TraverseToBlock(_state, x.NextBlock);
        }

        public override void VisitCFGConditionalEdge(ConditionalEdge x)
        {
            // build state for TrueBlock and FalseBlock properly, take minimal evaluation into account
            var state = _state;

            // true branch
            _state = state.Clone();
            OpAnalysis.AcceptCondition(x.Condition, ConditionBranch.ToTrue);
            TraverseToBlock(_state, x.TrueTarget);

            // false branch
            _state = state.Clone();
            OpAnalysis.AcceptCondition(x.Condition, ConditionBranch.ToFalse);
            TraverseToBlock(_state, x.FalseTarget);
        }

        public override void VisitCFGForeachEnumereeEdge(ForeachEnumereeEdge x)
        {
            Accept(x.Enumeree);
            VisitCFGSimpleEdge(x);
        }

        public override void VisitCFGForeachMoveNextEdge(ForeachMoveNextEdge x)
        {
            var state = _state;
            // get type information from Enumeree to determine types value variable
            var elementType = OpAnalysis.HandleTraversableUse(x.EnumereeEdge.Enumeree);
            if (elementType.IsVoid) elementType = TypeRefMask.AnyType;

            // Body branch
            _state = state.Clone();
            // set key variable and value variable at current state
            var valueVar = x.ValueVariable;
            if (valueVar.List == null)
            {
                var keyVar = x.KeyVariable;
                var dvar = x.ValueVariable.Variable as Syntax.AST.DirectVarUse;
                if (dvar != null)
                {
                    this.SetVar(dvar.VarName.Value, elementType);
                    if (x.KeyVariable != null)
                        _state.SetVarUsed(dvar.VarName.Value);    // do not report value variable as unused if there is also key variable. In PHP we can't enumerate keys without enumerating value var
                }

                if (x.KeyVariable != null)
                {
                    dvar = x.KeyVariable.Variable as Syntax.AST.DirectVarUse;
                    if (dvar != null)
                        this.SetVar(dvar.VarName.Value, TypeRefMask.AnyType);
                }
            }
            else
            {
                throw new NotImplementedException();
                //VisitListEx(valueVar.List, elementType);
            }
            TraverseToBlock(_state, x.BodyBlock);

            // End branch
            _state = state.Clone();
            TraverseToBlock(_state, x.NextBlock);
        }

        public override void VisitCFGSwitchEdge(SwitchEdge x)
        {
            Accept(x.SwitchValue);

            var state = _state;

            foreach (var c in x.CaseBlocks)
            {
                TraverseToBlock(state, c);
            }
        }

        public override void VisitCFGTryCatchEdge(TryCatchEdge x)
        {
            var state = _state;

            TraverseToBlock(state, x.BodyBlock);

            foreach (var c in x.CatchBlocks)
            {
                TraverseToBlock(state, c);
            }

            if (x.FinallyBlock != null)
            {
                TraverseToBlock(state, x.FinallyBlock);
            }
        }

        #endregion
    }
}
