# Blade Documentation Set

Hub for most documentation, including guides, formalisms, and proof explanations.

## The documents

| Document | Description | Canonical For |
|----------|-----|---------------|
| [formalism.md](formalism.md) | The language semantics: types, index types, loop objects, combinators, symmetry system, operational semantics, concrete syntax | What Blade programs *mean* |
| [proofs.md](proofs.md) | Theorem-by-theorem correspondence to the Coq proof stack (1362 theorems; the count is checked by `proofs/count-theorems.ps1 -Check`) | What is *proved*, and exactly how much |
| [features.md](features.md) | Catalog of every language feature with one-paragraph semantics and pointers | What Blade *has* |
| [quickstart-1.md](quickstart-1.md) | Quickstart part 1: basics through arity polymorphism and units | Tutorial |
| [quickstart-2.md](quickstart-2.md) | Quickstart part 2: advanced features | Tutorial |
| [examples.md](examples.md) | Worked end-to-end examples (each block compiled and run by `blade test docs`) | Cookbook |
| [features/sql.md](features/sql.md) | SQL-like / relational operations | Relational feature module |
| [features/equivariant-nn.md](features/equivariant-nn.md) | Equivariant ML: irreps, CG tensor products, spherical harmonics, message passing | ML feature module |
| [features/graphs-trees.md](features/graphs-trees.md) | Static trees (`TreeIdx<shape>`, implemented through P5) and graphs as adjacency data with `let rec` walks (designed) | Graph/tree feature module |
| [features/ppl.md](features/ppl.md) | Probabilistic programming: moment formers, Dist algebra, mstate streaming, inference | PPL feature module |
| [blade_literature_survey.md](blade_literature_survey.md) | Broad list of related literature and packages | Related work |
| [plans/README.md](plans/README.md) | Living design docs with per-doc status (active specs, executed plans, investigation verdicts) | Design history and intent |