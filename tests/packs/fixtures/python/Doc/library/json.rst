:mod:`json` --- JSON encoder and decoder
========================================

.. module:: json
   :synopsis: Encode and decode the JSON format.

.. class:: JSONDecoder(*, object_hook=None, strict=True)

   Simple JSON decoder.

   .. method:: decode(s)

      Return the Python representation of *s*.

.. exception:: JSONDecodeError(msg, doc, pos)

   Subclass of :exc:`ValueError` with additional attributes.
