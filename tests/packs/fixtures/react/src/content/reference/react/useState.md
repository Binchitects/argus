---
title: useState
---

<Intro>

`useState` is a React Hook that lets you add a [state variable](/learn/state-a-components-memory) to your component.

```js
const [state, setState] = useState(initialState)
```

</Intro>

<InlineToc />

---

## Reference {/*reference*/}

### `useState(initialState)` {/*usestate*/}

Call `useState` at the top level of your component.

```js
import { useState } from 'react';

function MyComponent() {
  const [age, setAge] = useState(28);
  // ...
}
```

#### Parameters {/*parameters*/}

#### Returns {/*returns*/}

### `set` functions, like `setSomething(nextState)` {/*setstate*/}

This heading is only partly a code span, so it is prose.

## Usage {/*usage*/}

### Adding state to a component {/*adding-state-to-a-component*/}

Some prose about state.

### `useReducer` {/*usereducer*/}

An API heading with no call signature.

### `npm install react` {/*install*/}

A code span that is not an API name.

### `useDeferredValue(value)`

An API heading with no explicit anchor.
