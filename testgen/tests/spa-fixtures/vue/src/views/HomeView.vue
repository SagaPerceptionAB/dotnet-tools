<script setup lang="ts">
document.title = 'Hem';
const featured = 3;
</script>

<template>
  <main>
    <h1>Välkommen</h1>
    <RouterLink to="/products">Produkter</RouterLink>
    <router-link :to="`/products/${featured}`">Veckans produkt</router-link>
    <router-link :to="{ name: 'about' }">Om oss</router-link>
    <a href="https://vuejs.org">Vue</a>
  </main>
</template>
