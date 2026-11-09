<script setup lang="ts">
import { onMounted, ref } from 'vue';

const products = ref<{ id: number; name: string }[]>([]);

onMounted(async () => {
  const response = await fetch('/api/products');
  products.value = await response.json();
});
</script>

<template>
  <section>
    <h1>Produkter</h1>
    <ul>
      <li v-for="p in products" :key="p.id">
        <router-link :to="'/products/' + p.id">{{ p.name }}</router-link>
      </li>
    </ul>
  </section>
</template>
